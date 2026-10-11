using System.Globalization;
using Anjal.Mime;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>One of a dashboard's periods, worked out in the viewer's zone (rc.15, item 56).</summary>
/// <param name="Key">today, yesterday, 7d, 30d, 3m or 12m.</param>
/// <param name="Label">Its name, for example "Last 7 days".</param>
/// <param name="Start">Its start.</param>
/// <param name="End">Its end (not included): now, or midnight for Yesterday.</param>
/// <param name="Step">How its time chart steps.</param>
public sealed record DashPeriod(string Key, string Label, DateTimeOffset Start, DateTimeOffset End, TimeStep Step);

/// <summary>One bar pair of a time chart (rc.15).</summary>
/// <param name="Label">Under the bar: 09, 2 Oct, Oct.</param>
/// <param name="Title">On hover: the whole step.</param>
/// <param name="Received">Received in it.</param>
/// <param name="Sent">Sent in it.</param>
/// <param name="Start">Where the step starts, in the viewer's zone.</param>
/// <param name="End">Where the next step starts, in the viewer's zone.</param>
public sealed record ChartBar(string Label, string Title, long Received, long Sent, DateTime Start = default, DateTime End = default);

/// <summary>One line of a "Needs attention" or similar box (rc.15).</summary>
/// <param name="Icon">A LiPicons name.</param>
/// <param name="Text">What it is.</param>
/// <param name="Sub">A second line, or empty.</param>
/// <param name="Href">Where it opens, or empty.</param>
/// <param name="Level">"", "warn" or "bad": the icon's colour.</param>
/// <param name="Args">Values for the blanks in Text and Sub, such as {n}; the words are looked up with their blanks, then filled.</param>
public sealed record DashLine(string Icon, string Text, string Sub, string Href, string Level = "", IReadOnlyDictionary<string, string>? Args = null)
{
    /// <summary>Blanks for one count, {n}.</summary>
    /// <param name="n">The count.</param>
    /// <returns>The blanks.</returns>
    public static IReadOnlyDictionary<string, string> N(long n) => new Dictionary<string, string>(StringComparer.Ordinal) { ["n"] = n.ToString("N0", CultureInfo.InvariantCulture) };
}

/// <summary>One sender of a domain's mail, from DMARC reports (rc.15, item 32, "who sends as").</summary>
/// <param name="Name">Who: Anjal, or the address.</param>
/// <param name="Sub">How it fared.</param>
/// <param name="Count">Messages.</param>
/// <param name="Status">"pass", "fix" or "blocked".</param>
public sealed record SenderLine(string Name, string Sub, long Count, string Status);

/// <summary>
/// The dashboards (rc.15, items 56 to 63): the periods, the figures for a
/// mailbox, an organisation or the service, and the boxes beside them.
/// Each viewer sees only what they are entitled to: a person their own
/// mailbox (and shared ones they hold a right on), an administrator totals
/// for their organisation, an operator totals per organisation. No one's
/// messages are shown above the person's own.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The DMARC reports' folder (item 31).</summary>
    public const string DmarcFolder = "DMARC reports";

    /// <summary>The kind of the organisation document holding reports already read, by message.</summary>
    public const string DmarcKind = "dmarc";

    /// <summary>The periods, in order (item 56).</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> DashPeriods = new[]
    {
        ("today", "Today"),
        ("yesterday", "Yesterday"),
        ("7d", "Last 7 days"),
        ("30d", "Last 30 days"),
        ("3m", "Last 3 months"),
        ("12m", "Last 12 months"),
    };

    private static readonly string[] NoReplyWords = { "noreply", "no-reply", "donotreply", "do-not-reply", "mailer-daemon", "postmaster", "notifications", "newsletter", "bounce" };

    /// <summary>A period by its key, in the viewer's zone; Today when the key is unknown.</summary>
    /// <param name="key">The key.</param>
    /// <param name="clock">The viewer's zone.</param>
    /// <param name="now">Now.</param>
    /// <returns>The period.</returns>
    public static DashPeriod DashPeriodOf(string? key, ZonedClock clock, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(clock);
        DateTime today = clock.Local(now).Date;
        DateTimeOffset midnight = new(TimeZoneInfo.ConvertTimeToUtc(today, clock.Zone), TimeSpan.Zero);

        // DES-11 D8: one day of the last year, opened from a point of the 30-day activity chart.
        if (key is not null && key.StartsWith("day:", StringComparison.Ordinal)
            && DateTime.TryParseExact(key[4..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day)
            && day <= today && day > today.AddYears(-1))
        {
            DateTimeOffset start = new(TimeZoneInfo.ConvertTimeToUtc(day, clock.Zone), TimeSpan.Zero);
            DateTimeOffset end = day == today ? now : new(TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1), clock.Zone), TimeSpan.Zero);
            return new DashPeriod("day:" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "That day", start, end, TimeStep.Hour);
        }
        return key switch
        {
            "yesterday" => new DashPeriod("yesterday", "Yesterday", new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(today.AddDays(-1), clock.Zone), TimeSpan.Zero), midnight, TimeStep.Hour),
            "7d" => new DashPeriod("7d", "Last 7 days", now.AddDays(-7), now, TimeStep.Day),
            "30d" => new DashPeriod("30d", "Last 30 days", now.AddDays(-30), now, TimeStep.Day),
            "3m" => new DashPeriod("3m", "Last 3 months", now.AddMonths(-3), now, TimeStep.Week),
            "12m" => new DashPeriod("12m", "Last 12 months", now.AddMonths(-12), now, TimeStep.Month),
            _ => new DashPeriod("today", "Today", midnight, now, TimeStep.Hour),
        };
    }

    /// <summary>The figures for a scope and period.</summary>
    /// <param name="scope">A mailbox, an organisation, or the service.</param>
    /// <param name="period">The period.</param>
    /// <param name="clock">The viewer's zone.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The figures.</returns>
    public Task<MailFigures> FiguresAsync(FigureScope scope, DashPeriod period, ZonedClock clock, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(clock);
        return this.store.GetMailFiguresAsync(scope, period.Start, period.End, clock.ZoneId, period.Step, ct);
    }

    /// <summary>The figures for every period, for the counts table.</summary>
    /// <param name="scope">A mailbox, an organisation, or the service.</param>
    /// <param name="clock">The viewer's zone.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Each period with its figures, in order.</returns>
    public async Task<IReadOnlyList<(DashPeriod Period, MailFigures Figures)>> FiguresTableAsync(FigureScope scope, ZonedClock clock, DateTimeOffset now, CancellationToken ct = default)
    {
        var rows = new List<(DashPeriod, MailFigures)>();
        foreach ((string key, string _) in DashPeriods)
        {
            DashPeriod p = DashPeriodOf(key, clock, now);
            rows.Add((p, await this.FiguresAsync(scope, p, clock, ct).ConfigureAwait(false)));
        }
        return rows;
    }

    /// <summary>The bars of a time chart: every step of the period, with nothing where nothing happened.</summary>
    /// <param name="figures">The figures.</param>
    /// <param name="period">The period.</param>
    /// <param name="clock">The viewer's zone.</param>
    /// <returns>The bars, oldest first.</returns>
    public static IReadOnlyList<ChartBar> Bars(MailFigures figures, DashPeriod period, ZonedClock clock)
    {
        ArgumentNullException.ThrowIfNull(figures);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(clock);
        Dictionary<DateTime, TimeBucket> have = figures.Series.ToDictionary(b => b.Start);
        DateTime first = InMemoryMessageStore.StepStart(clock.Local(period.Start).DateTime, period.Step);
        DateTime last = clock.Local(period.End.AddTicks(-1)).DateTime;
        var bars = new List<ChartBar>();
        for (DateTime at = first; at <= last && bars.Count < 400; at = Next(at, period.Step))
        {
            have.TryGetValue(at, out TimeBucket? b);
            (string label, string title) = period.Step switch
            {
                TimeStep.Hour => (at.ToString("HH", CultureInfo.InvariantCulture), at.ToString("HH:00", CultureInfo.InvariantCulture) + "-" + at.AddHours(1).ToString("HH:00", CultureInfo.InvariantCulture)),
                TimeStep.Day => (at.ToString("d MMM", CultureInfo.InvariantCulture), at.ToString("ddd d MMM", CultureInfo.InvariantCulture)),
                TimeStep.Week => (at.ToString("d MMM", CultureInfo.InvariantCulture), "Week from " + at.ToString("d MMM", CultureInfo.InvariantCulture)),
                _ => (at.ToString("MMM", CultureInfo.InvariantCulture), at.ToString("MMMM yyyy", CultureInfo.InvariantCulture)),
            };
            bars.Add(new ChartBar(label, title, b?.Received ?? 0, b?.Sent ?? 0, at, Next(at, period.Step)));
        }
        return bars;
    }

    /// <summary>What a mailbox received in a period, by folder, category and sender.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="period">The period.</param>
    /// <param name="clock">The viewer's zone.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The activity.</returns>
    public Task<MailboxActivity> ActivityInAsync(Guid mailboxId, DashPeriod period, ZonedClock clock, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(clock);
        return this.store.GetActivityAsync(mailboxId, period.Start, period.End, clock.ZoneId, ct);
    }

    /// <summary>
    /// Messages the person sent that have had no reply for 3 days (item 63):
    /// sent to a person in To - not lists, not no-reply addresses, not Cc
    /// only - in the last 30 days, at least 3 days ago, with nothing received
    /// in the conversation since; short closing messages left out.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many.</returns>
    public async Task<int> NoReplyCountAsync(Guid mailboxId, DateTimeOffset now, CancellationToken ct = default) =>
        (await this.NoReplyMessagesAsync(mailboxId, now, ct).ConfigureAwait(false)).Count;

    /// <summary>
    /// rc.15 (item 63): the messages behind the "No reply" count, newest first - sent to a person,
    /// three to thirty days ago, with no answer in the conversation. "Expect a reply" always
    /// counts a message; "No reply needed" takes it out.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyList<MessageRow>> NoReplyMessagesAsync(Guid mailboxId, DateTimeOffset now, CancellationToken ct = default)
    {
        FolderRow? sent = await this.GetFolderAsync(mailboxId, "Sent", ct).ConfigureAwait(false);
        if (sent is null)
        {
            return Array.Empty<MessageRow>();
        }
        IReadOnlyList<MessageRow> rows = await this.store.ListMessagesAsync(mailboxId, sent.Id, 300, 0, ct).ConfigureAwait(false);
        IReadOnlyDictionary<string, IReadOnlyList<ConversationItem>> index = await this.ConversationIndexAsync(mailboxId, ct).ConfigureAwait(false);
        ReplyMarks marks = await this.GetReplyMarksAsync(mailboxId, ct).ConfigureAwait(false);
        var waiting = new List<MessageRow>();
        foreach (MessageRow m in rows)
        {
            if (m.ReceivedAt > now.AddDays(-3) || m.ReceivedAt < now.AddDays(-30) || marks.NoReplyNeeded.Contains(m.Id))
            {
                continue;
            }
            if (!Expected(marks, m) && (!ToAPerson(m.ToHeader) || IsClosingLine(m.Preview)))
            {
                continue;
            }
            string key = ConversationKey(m.Subject);
            bool replied = key.Length > 0 && index.TryGetValue(key, out IReadOnlyList<ConversationItem>? items)
                && items.Any(i => i.At > m.ReceivedAt && i.Folder != "Sent");
            if (!replied)
            {
                waiting.Add(m);
            }
        }
        return waiting;
    }

    /// <summary>The person's "Needs attention" (item 58): no reply, drafts, the Outbox, the scheduled.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="clock">The viewer's zone.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The lines; empty when nothing needs the person.</returns>
    public async Task<IReadOnlyList<DashLine>> PersonAttentionAsync(Guid mailboxId, ZonedClock clock, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var lines = new List<DashLine>();
        int noReply = await this.NoReplyCountAsync(mailboxId, now, ct).ConfigureAwait(false);
        if (noReply > 0)
        {
            lines.Add(new DashLine("undo", noReply == 1 ? "1 of your messages has had no reply for 3 days" : "{n} of your messages have had no reply for 3 days", "Sent by you to a person; lists, no-reply addresses and Cc left out", "/no-reply", string.Empty, DashLine.N(noReply)));
        }
        IReadOnlyList<FolderView> folders = await this.ListFoldersAsync(mailboxId, ct).ConfigureAwait(false);
        long drafts = folders.FirstOrDefault(f => f.Name == "Drafts")?.Count ?? 0;
        if (drafts > 0)
        {
            lines.Add(new DashLine("edit", drafts == 1 ? "1 draft not finished" : "{n} drafts not finished", string.Empty, "/folder/Drafts", string.Empty, DashLine.N(drafts)));
        }
        IReadOnlyList<OutboxItem> outbox = await this.ListOutboxAsync(mailboxId, ct).ConfigureAwait(false);
        if (outbox.Count > 0)
        {
            OutboxItem first = outbox[0];
            var args = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["n"] = outbox.Count.ToString(CultureInfo.InvariantCulture),
                ["why"] = Clip(first.LastReply, 60),
                ["time"] = clock.Time(first.NextTry),
            };
            lines.Add(new DashLine("upload", outbox.Count == 1 ? "1 in the Outbox, retrying" : "{n} in the Outbox, retrying", first.LastReply.Length > 0 ? "{why}; next try {time}" : "Next try {time}", "/outbox", "warn", args));
        }
        long scheduled = folders.FirstOrDefault(f => f.Name == ScheduledFolder)?.Count ?? 0;
        if (scheduled > 0)
        {
            lines.Add(new DashLine("clock", scheduled == 1 ? "1 message waiting for its send time" : "{n} messages waiting for their send time", string.Empty, "/folder/" + ScheduledFolder, string.Empty, DashLine.N(scheduled)));
        }
        return lines;
    }

    /// <summary>The newest unread messages in the Inbox, and how many there are.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="take">How many to list.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The count and the messages.</returns>
    public async Task<(long Count, IReadOnlyList<MessageRow> Newest)> UnreadAsync(Guid mailboxId, int take, CancellationToken ct = default)
    {
        FolderRow? inbox = await this.GetFolderAsync(mailboxId, FolderRow.Inbox, ct).ConfigureAwait(false);
        if (inbox is null)
        {
            return (0, Array.Empty<MessageRow>());
        }
        (IReadOnlyList<MessageRow> items, long total) = await this.ListFilteredAsync(mailboxId, inbox.Id, "unread", 0, Math.Clamp(take, 1, 20), ct).ConfigureAwait(false);
        return (total, items);
    }

    /// <summary>Refused sign-ins to some addresses since a moment, from the activity log.</summary>
    /// <param name="addresses">The addresses; null for everyone.</param>
    /// <param name="since">From when.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many.</returns>
    public async Task<int> FailedSignInsAsync(IReadOnlyCollection<string>? addresses, DateTimeOffset since, CancellationToken ct = default)
    {
        var set = addresses is null ? null : new HashSet<string>(addresses, StringComparer.OrdinalIgnoreCase);
        int count = 0;
        DateTimeOffset? before = null;
        for (int page = 0; page < 20; page++)
        {
            IReadOnlyList<AuditEvent> batch = await this.messageStore.ListAuditAsync(1000, before, ct).ConfigureAwait(false);
            foreach (AuditEvent e in batch)
            {
                if (e.At < since)
                {
                    return count;
                }
                bool refused = e.Action.StartsWith("webmail.signin", StringComparison.Ordinal)
                    && (e.Action.EndsWith("failed", StringComparison.Ordinal) || e.Action.EndsWith("throttled", StringComparison.Ordinal));
                if (refused && (set is null || set.Contains(e.Subject) || set.Contains(e.Actor)))
                {
                    count++;
                }
            }
            if (batch.Count < 1000)
            {
                break;
            }
            before = batch[^1].At;
        }
        return count;
    }

    /// <summary>
    /// Who was stopped for too many sign-in tries since a moment (item 59,
    /// "locked-out people"): each address or user name once, newest first.
    /// </summary>
    /// <param name="addresses">The addresses and user names to look for.</param>
    /// <param name="since">From when.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The addresses and user names, as the log records them.</returns>
    public async Task<IReadOnlyList<string>> StoppedForTriesAsync(IReadOnlyCollection<string> addresses, DateTimeOffset since, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        var set = new HashSet<string>(addresses, StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        DateTimeOffset? before = null;
        for (int page = 0; page < 20; page++)
        {
            IReadOnlyList<AuditEvent> batch = await this.messageStore.ListAuditAsync(1000, before, ct).ConfigureAwait(false);
            foreach (AuditEvent e in batch)
            {
                if (e.At < since)
                {
                    return found;
                }
                if (e.Action == "webmail.signin.throttled" && set.Contains(e.Subject) && !found.Contains(e.Subject, StringComparer.OrdinalIgnoreCase))
                {
                    found.Add(e.Subject);
                }
            }
            if (batch.Count < 1000)
            {
                break;
            }
            before = batch[^1].At;
        }
        return found;
    }

    /// <summary>An organisation's "Needs attention" (item 59).</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="people">Its people.</param>
    /// <param name="clock">The viewer's time zone, for dates; UTC when null.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The lines.</returns>
    public async Task<IReadOnlyList<DashLine>> OrgAttentionAsync(TenantRow tenant, IReadOnlyList<PersonView> people, ZonedClock? clock = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(people);
        var lines = new List<DashLine>();
        foreach (TenantDomainRow d in await this.store.ListTenantDomainsAsync(tenant.Id, ct).ConfigureAwait(false))
        {
            if (await this.messageStore.GetDkimKeyAsync(d.Domain, ct).ConfigureAwait(false) is null)
            {
                lines.Add(new DashLine("world-route", "No DKIM key for {domain}", "Mail sent from it may be refused", "/org/domains?domain=" + Uri.EscapeDataString(d.Domain), "bad", new Dictionary<string, string>(StringComparer.Ordinal) { ["domain"] = d.Domain }));
            }
        }
        // rc.15 (item 59): records the daily check found missing or wrong.
        if (await this.DnsCheckOfAsync(tenant.Id, ct).ConfigureAwait(false) is DnsCheckRecord check)
        {
            foreach ((string domain, Dictionary<string, string> found) in check.Domains)
            {
                List<string> wrong = CheckedRecords.Where(k => found.TryGetValue(k, out string? s) && s is "missing" or "different").ToList();
                if (wrong.Count > 0)
                {
                    string href = $"/org/domains?domain={Uri.EscapeDataString(domain)}&check=1";
                    lines.Add(new DashLine("world-route", "{domain}: {records} not right", "Found by the daily check; mail may be refused or marked as spam", href, "bad",
                        new Dictionary<string, string>(StringComparer.Ordinal) { ["domain"] = domain, ["records"] = string.Join(", ", wrong) }));
                }
            }
        }
        List<PersonView> disabled = people.Where(p => p.Status == "disabled").ToList();
        if (disabled.Count > 0)
        {
            lines.Add(new DashLine("lock", disabled.Count == 1 ? "1 person disabled" : "{n} people disabled", "{who}", "/org/people?f=disabled", string.Empty,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["n"] = disabled.Count.ToString(CultureInfo.InvariantCulture), ["who"] = string.Join(", ", disabled.Take(3).Select(p => NameOf(p.Mailbox))) }));
        }
        List<PersonView> mustChange = people.Where(p => p.Status == "must-change").ToList();
        if (mustChange.Count > 0)
        {
            lines.Add(new DashLine("lock", mustChange.Count == 1 ? "1 person must change their password" : "{n} people must change their password", "{who}", "/org/people", "warn",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["n"] = mustChange.Count.ToString(CultureInfo.InvariantCulture), ["who"] = string.Join(", ", mustChange.Take(3).Select(p => NameOf(p.Mailbox))) }));
        }
        // Item 59: people stopped for too many sign-in tries in the last day.
        IReadOnlyList<string> stopped = await this.StoppedForTriesAsync(people.SelectMany(p => new[] { p.Mailbox.Address, p.Mailbox.LocalPart }).ToList(), DateTimeOffset.UtcNow.AddDays(-1), ct).ConfigureAwait(false);
        List<PersonView> lockedOut = people.Where(p => stopped.Contains(p.Mailbox.Address, StringComparer.OrdinalIgnoreCase) || stopped.Contains(p.Mailbox.LocalPart, StringComparer.OrdinalIgnoreCase)).ToList();
        if (lockedOut.Count > 0)
        {
            lines.Add(new DashLine("lock", lockedOut.Count == 1 ? "1 person stopped for too many sign-in tries today" : "{n} people stopped for too many sign-in tries today", "{who}", "/org/log?kind=sign-in&days=1", "warn",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["n"] = lockedOut.Count.ToString(CultureInfo.InvariantCulture), ["who"] = string.Join(", ", lockedOut.Take(3).Select(p => NameOf(p.Mailbox))) }));
        }
        // Item 59: full mailboxes first, then over 90%; over 80% is in the Storage box.
        int full = people.Count(p => Fullness(p.Mailbox) >= 100);
        if (full > 0)
        {
            lines.Add(new DashLine("box", full == 1 ? "1 mailbox full" : "{n} mailboxes full", "Give more storage or ask them to clear mail", "/org/people", "bad", DashLine.N(full)));
        }
        int full90 = people.Count(p => Fullness(p.Mailbox) >= 90 && Fullness(p.Mailbox) < 100);
        if (full90 > 0)
        {
            lines.Add(new DashLine("box", full90 == 1 ? "1 mailbox over 90% full" : "{n} mailboxes over 90% full", string.Empty, "/org/people", "warn", DashLine.N(full90)));
        }
        int invited = people.Count(p => p.Status == "invited");
        if (invited > 0)
        {
            lines.Add(new DashLine("user", invited == 1 ? "1 invitation not accepted" : "{n} invitations not accepted", string.Empty, "/org/people?f=invited", string.Empty, DashLine.N(invited)));
        }
        foreach (OrgApp app in await this.AppsOfAsync(tenant.Id, ct).ConfigureAwait(false))
        {
            if (app.Keys.Count == 0)
            {
                lines.Add(new DashLine("lock", "{app} has no key", "It cannot send until it has one", "/org/apps?sel=" + Uri.EscapeDataString(app.Id), "warn", new Dictionary<string, string>(StringComparer.Ordinal) { ["app"] = app.Name }));
            }
            else if (app.Keys.Any(k => k.EndsAt is not null))
            {
                DateTimeOffset ends = app.Keys.Where(k => k.EndsAt is not null).Min(k => k.EndsAt!.Value);
                lines.Add(new DashLine("lock", "{app}: an old key stops working {date}", "Move the application to its new key before then", "/org/apps?sel=" + Uri.EscapeDataString(app.Id), string.Empty,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["app"] = app.Name, ["date"] = (clock is null ? ends : clock.Local(ends)).ToString("d MMM", CultureInfo.InvariantCulture) }));
            }
        }
        return lines;
    }

    /// <summary>
    /// Who sends as an organisation's domains (item 32), from the DMARC
    /// aggregate reports received in its mailboxes in a period: each sending
    /// address, how many messages, and whether DMARC passed. Each report is
    /// read once and kept, by message, in the organisation's documents.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="period">The period.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The senders, most messages first, and how many reports they come from.</returns>
    public async Task<(IReadOnlyList<SenderLine> Lines, int Reports)> WhoSendsAsAsync(TenantRow tenant, DashPeriod period, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(period);
        Dictionary<Guid, List<DmarcRow>> read = await this.ReadTenantDocumentAsync<Dictionary<Guid, List<DmarcRow>>>(tenant.Id, DmarcKind, ct).ConfigureAwait(false) ?? new Dictionary<Guid, List<DmarcRow>>();
        bool changed = false;
        HashSet<string> ours = (await this.store.ListTenantDomainsAsync(tenant.Id, ct).ConfigureAwait(false)).Select(d => d.Domain.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var rows = new List<DmarcRow>();
        int reports = 0;
        foreach (MailboxRow box in await this.store.ListMailboxesAsync(tenant.Id, ct).ConfigureAwait(false))
        {
            // Reports are in their own folder once the DMARC rule is on (item 31); before
            // that, wherever they landed, known by the subject receivers give them.
            var messages = new List<MessageRow>();
            foreach (string name in new[] { DmarcFolder, FolderRow.Inbox, Anjal.Mailbox.MailboxSink.JunkFolder })
            {
                if (await this.GetFolderAsync(box.Id, name, ct).ConfigureAwait(false) is FolderRow folder)
                {
                    messages.AddRange((await this.store.ListMessagesAsync(box.Id, folder.Id, 500, 0, ct).ConfigureAwait(false))
                        .Where(m => name == DmarcFolder || EncodedWordDecoder.Decode(m.Subject).TrimStart().StartsWith("Report domain:", StringComparison.OrdinalIgnoreCase)));
                }
            }
            foreach (MessageRow m in messages.Where(m => m.ReceivedAt >= period.Start.AddDays(-2) && m.ReceivedAt < period.End.AddDays(2)))
            {
                if (!read.TryGetValue(m.Id, out List<DmarcRow>? found))
                {
                    found = new List<DmarcRow>();
                    foreach (AttachmentView a in await this.ListAttachmentsAsync(box.Id, m.Id, ct).ConfigureAwait(false) ?? Array.Empty<AttachmentView>())
                    {
                        if (DmarcReports.MayBeReport(a.FileName, a.ContentType) && await this.GetAttachmentAsync(box.Id, m.Id, a.Index, ct).ConfigureAwait(false) is { } got
                            && DmarcReports.Read(got.Bytes) is DmarcReport report)
                        {
                            found.AddRange(report.Rows);
                        }
                    }
                    read[m.Id] = found;
                    changed = true;
                }
                List<DmarcRow> mine = found.Where(r => ours.Contains(r.HeaderFrom)).ToList();
                if (mine.Count > 0)
                {
                    reports++;
                    rows.AddRange(mine);
                }
            }
        }
        if (changed)
        {
            // Keep the newest thousand messages' readings; older ones are read again only if asked for.
            await this.WriteTenantDocumentAsync(tenant.Id, DmarcKind, read.TakeLast(1000).ToDictionary(kv => kv.Key, kv => kv.Value), ct).ConfigureAwait(false);
        }
        HashSet<string> ourAddresses = await this.ServiceAddressesAsync(ct).ConfigureAwait(false);
        IReadOnlyList<SenderLine> lines = rows
            .GroupBy(r => r.SourceIp)
            .Select(g =>
            {
                long total = g.Sum(r => r.Count);
                long passed = g.Where(r => r.Passed).Sum(r => r.Count);
                bool dkimFails = g.Any(r => r.Dkim != "pass");
                bool spfFails = g.Any(r => r.Spf != "pass");
                bool refused = g.Any(r => !r.Passed && r.Disposition is "reject" or "quarantine");
                string status = passed == total ? (dkimFails || spfFails ? "fix" : "pass") : refused ? "blocked" : "fix";
                string name = ourAddresses.Contains(g.Key) ? "Anjal, " + this.hostName : "Unknown, " + g.Key;
                string sub = status switch
                {
                    "pass" => ourAddresses.Contains(g.Key) ? "Your own mail" : "SPF and DKIM pass",
                    "blocked" => "Forged: refused by receivers",
                    _ => (spfFails ? "SPF fails" : "SPF pass") + ", " + (dkimFails ? "DKIM fails" : "DKIM pass"),
                };
                return new SenderLine(name, sub, total, status);
            })
            .OrderByDescending(l => l.Count)
            .ToList();
        return (lines, reports);
    }

    /// <summary>Outbound mail from some addresses in a period (the applications box).</summary>
    /// <param name="senders">The addresses.</param>
    /// <param name="period">The period.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Each address's traffic.</returns>
    public Task<IReadOnlyList<SenderTraffic>> OutboundBySenderAsync(IReadOnlyCollection<string> senders, DashPeriod period, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(senders);
        ArgumentNullException.ThrowIfNull(period);
        return this.messageStore.CountOutboundBySenderAsync(senders, period.Start, period.End, ct);
    }

    /// <summary>Messages received per organisation in a period (the operator's dashboard).</summary>
    /// <param name="period">The period.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>By organisation.</returns>
    public Task<IReadOnlyDictionary<Guid, long>> ReceivedByOrganisationAsync(DashPeriod period, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(period);
        return this.store.CountReceivedByTenantAsync(period.Start, period.End, ct);
    }

    /// <summary>Operator actions (the Anjal console's) since a moment, from the activity log.</summary>
    /// <param name="since">From when.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many.</returns>
    public async Task<int> OperatorActionsAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        int count = 0;
        DateTimeOffset? before = null;
        for (int page = 0; page < 20; page++)
        {
            IReadOnlyList<AuditEvent> batch = await this.messageStore.ListAuditAsync(1000, before, ct).ConfigureAwait(false);
            foreach (AuditEvent e in batch)
            {
                if (e.At < since)
                {
                    return count;
                }
                if (e.Action.StartsWith("ops.", StringComparison.Ordinal))
                {
                    count++;
                }
            }
            if (batch.Count < 1000)
            {
                break;
            }
            before = batch[^1].At;
        }
        return count;
    }

    /// <summary>How full a mailbox is, in percent of its quota (0 when it has none).</summary>
    /// <param name="mailbox">The mailbox.</param>
    /// <returns>The percentage.</returns>
    public static int Fullness(MailboxRow mailbox)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        return Sizes.Percent(mailbox.UsedBytes, mailbox.QuotaBytes);
    }

    private static string NameOf(MailboxRow m) => m.DisplayName.Trim().Length > 0 ? m.DisplayName.Trim() : m.Address;

    private static Func<DateTime, DateTime> Stepper(TimeStep step) => step switch
    {
        TimeStep.Hour => d => d.AddHours(1),
        TimeStep.Day => d => d.AddDays(1),
        TimeStep.Week => d => d.AddDays(7),
        _ => d => d.AddMonths(1),
    };

    private static DateTime Next(DateTime at, TimeStep step) => Stepper(step)(at);

    // A person in To: at least one address that is not a list or a no-reply address.
    private static bool ToAPerson(string toHeader)
    {
        IReadOnlyList<MailAddress> to = AddressParser.Parse(EncodedWordDecoder.Decode(toHeader ?? string.Empty));
        return to.Any(a =>
        {
            string local = a.Address.Split('@')[0].ToLowerInvariant();
            return !NoReplyWords.Any(w => local.Contains(w, StringComparison.Ordinal)) && !local.EndsWith("-list", StringComparison.Ordinal) && !local.StartsWith("list", StringComparison.Ordinal);
        });
    }

    // "Thanks!", "Noted", "OK, will do": a short closing line expects no reply.
    private static bool IsClosingLine(string preview)
    {
        string p = (preview ?? string.Empty).Trim();
        return p.Length < 40 && (p.Length == 0 || p.StartsWith("thank", StringComparison.OrdinalIgnoreCase) || p.StartsWith("noted", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("ok", StringComparison.OrdinalIgnoreCase) || p.StartsWith("received", StringComparison.OrdinalIgnoreCase) || p.StartsWith("got it", StringComparison.OrdinalIgnoreCase));
    }

    // The service's own sending addresses, for "Anjal" in "who sends as".
    private async Task<HashSet<string>> ServiceAddressesAsync(CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            foreach (System.Net.IPAddress ip in await System.Net.Dns.GetHostAddressesAsync(this.hostName, timeout.Token).ConfigureAwait(false))
            {
                set.Add(ip.ToString());
            }
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // Not resolvable here: every sender shows by its address.
        }
        return set;
    }
}
