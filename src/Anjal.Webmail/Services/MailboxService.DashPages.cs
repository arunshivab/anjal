using System.Globalization;
using Anjal.Mime;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>One step of a time series: received (not Junk), sent and Junk, and the same step of the period before.</summary>
/// <param name="Start">Where the step starts.</param>
/// <param name="End">Where it ends.</param>
/// <param name="Label">Its label on the axis.</param>
/// <param name="Received">Received, not counting Junk.</param>
/// <param name="Sent">Sent.</param>
/// <param name="Junk">Went to Junk.</param>
/// <param name="Previous">Received in the same step of the period before.</param>
public sealed record SeriesPoint(DateTimeOffset Start, DateTimeOffset End, string Label, long Received, long Sent, long Junk, long Previous);

/// <summary>A count with a name, where it leads and a colour slot.</summary>
/// <param name="Name">The name shown.</param>
/// <param name="Count">The count (or bytes, or minutes, as the card says).</param>
/// <param name="Link">What it opens, or null.</param>
/// <param name="Slot">A category colour slot, or 0.</param>
/// <param name="Trend">A small trend, oldest first, or empty.</param>
public sealed record NamedFigure(string Name, long Count, string? Link, int Slot, IReadOnlyList<long> Trend);

/// <summary>Someone the person writes with: messages from them, from the person, how often the person replied and how quickly.</summary>
/// <param name="Address">Their address.</param>
/// <param name="Name">Their name, or the address.</param>
/// <param name="FromThem">Messages received from them.</param>
/// <param name="FromYou">Messages the person sent them.</param>
/// <param name="RepliedPercent">Of their messages, the share the person answered; null when none came.</param>
/// <param name="UsualReply">The person's usual reply time to them, or null.</param>
public sealed record PersonTraffic(string Address, string Name, long FromThem, long FromYou, int? RepliedPercent, TimeSpan? UsualReply);

/// <summary>Everything the six dashboard pages show for one mailbox and period (design review section 7).</summary>
public sealed class DashPages
{
    /// <summary>The period.</summary>
    public DashPeriod Period { get; init; } = null!;

    /// <summary>The period before it, of the same length.</summary>
    public DashPeriod Previous { get; init; } = null!;

    /// <summary>Received (not Junk), this period and the one before.</summary>
    public (long Now, long Before) Received { get; init; }

    /// <summary>Sent, this period and the one before.</summary>
    public (long Now, long Before) Sent { get; init; }

    /// <summary>Junk caught, this period and the one before.</summary>
    public (long Now, long Before) Junk { get; init; }

    /// <summary>Unread in the Inbox now, and how many of those are older than a week.</summary>
    public (long Now, long OlderThanAWeek) Unread { get; init; }

    /// <summary>Waiting for a reply (sent, no answer after three days), and how many for more than three days more.</summary>
    public (long Now, long Long) Waiting { get; init; }

    /// <summary>The usual reply time this period and the one before (null when there were no replies).</summary>
    public (TimeSpan? Now, TimeSpan? Before) UsualReply { get; init; }

    /// <summary>Received, sent and Junk over the period, step by step.</summary>
    public IReadOnlyList<SeriesPoint> Series { get; init; } = Array.Empty<SeriesPoint>();

    /// <summary>Daily trends over the last 14 days: received, sent, unread arrivals, reply minutes, waiting, Junk.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<long>> Trends { get; init; } = new Dictionary<string, IReadOnlyList<long>>();

    /// <summary>Received by weekday (Monday first) and hour, in the person's zone.</summary>
    public long[,] Heat { get; init; } = new long[7, 24];

    /// <summary>Received from inside the organisation and from outside it.</summary>
    public (long Inside, long Outside) InsideOutside { get; init; }

    /// <summary>Received by folder.</summary>
    public IReadOnlyList<NamedFigure> ByFolder { get; init; } = Array.Empty<NamedFigure>();

    /// <summary>Received by category, with a trend each.</summary>
    public IReadOnlyList<NamedFigure> ByCategory { get; init; } = Array.Empty<NamedFigure>();

    /// <summary>Attachments by kind of file (PDF, DICOM ...), from the newest messages that carry them.</summary>
    public IReadOnlyList<NamedFigure> AttachmentKinds { get; init; } = Array.Empty<NamedFigure>();

    /// <summary>How many messages the attachment kinds were read from.</summary>
    public int AttachmentSample { get; init; }

    /// <summary>Mailing lists and notifications: messages from each, most first.</summary>
    public IReadOnlyList<NamedFigure> Lists { get; init; } = Array.Empty<NamedFigure>();

    /// <summary>Messages from lists and notifications in the period.</summary>
    public long ListMessages { get; init; }

    /// <summary>The people the person writes with most, both ways.</summary>
    public IReadOnlyList<PersonTraffic> People { get; init; } = Array.Empty<PersonTraffic>();

    /// <summary>Senders whose first message in a year came in this period.</summary>
    public IReadOnlyList<NamedFigure> NewSenders { get; init; } = Array.Empty<NamedFigure>();

    /// <summary>Received by the sender's organisation (domain).</summary>
    public IReadOnlyList<NamedFigure> ByDomain { get; init; } = Array.Empty<NamedFigure>();

    /// <summary>Replies by how long they took: within an hour, the same half-day, the next day, later.</summary>
    public IReadOnlyList<long> ReplyBands { get; init; } = new long[4];

    /// <summary>The usual reply time to people inside the organisation, and outside it.</summary>
    public (TimeSpan? Inside, TimeSpan? Outside) ReplyInsideOutside { get; init; }

    /// <summary>Received mail to the person, not answered after two days (newest first, at most 50).</summary>
    public IReadOnlyList<MessageFact> NotAnswered { get; init; } = Array.Empty<MessageFact>();

    /// <summary>Unread mail left in the Inbox, by the day it arrived (oldest first, over the period).</summary>
    public IReadOnlyList<SeriesPoint> UnreadBacklog { get; init; } = Array.Empty<SeriesPoint>();

    /// <summary>Of the mail received, the share from senders whose checks passed (when checked).</summary>
    public (long Verified, long Checked) Verified { get; init; }

    /// <summary>Of the mail received from outside, the share that arrived encrypted.</summary>
    public (long Encrypted, long Known) Encrypted { get; init; }

    /// <summary>Senders whose mail arrived unencrypted in the period, most first.</summary>
    public IReadOnlyList<NamedFigure> UnencryptedSenders { get; init; } = Array.Empty<NamedFigure>();

    /// <summary>Space by folder, largest first, in bytes.</summary>
    public IReadOnlyList<NamedFigure> Space { get; init; } = Array.Empty<NamedFigure>();

    /// <summary>The mailbox's limit in bytes (0 when none) and what is used.</summary>
    public (long Used, long Quota) Storage { get; init; }

    /// <summary>Space used at the end of each of the last 12 months, oldest first.</summary>
    public IReadOnlyList<NamedFigure> Growth { get; init; } = Array.Empty<NamedFigure>();

    /// <summary>At the last 3 months' rate, about how many months until the mailbox is full; null when not growing or no limit.</summary>
    public int? FullInMonths { get; init; }

    /// <summary>The biggest messages, largest first.</summary>
    public IReadOnlyList<MessageFact> Biggest { get; init; } = Array.Empty<MessageFact>();

    /// <summary>Senders whose mail takes most space.</summary>
    public IReadOnlyList<NamedFigure> BiggestSenders { get; init; } = Array.Empty<NamedFigure>();
}

/// <summary>The six dashboard pages (rc.16, design review section 7, owner 11 Oct 2026).</summary>
public sealed partial class MailboxService
{
    /// <summary>The most messages a period's figures are worked out from.</summary>
    public const int DashFactLimit = 50000;

    // The attachment kinds are read from at most this many of the newest messages that carry them.
    private const int AttachmentSampleLimit = 200;

    /// <summary>The page-level periods (design review 7): Today, 7 days, 30 days, 90 days, 12 months.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> DashPagePeriods = new[]
    {
        ("today", "Today"),
        ("7d", "7 days"),
        ("30d", "30 days"),
        ("90d", "90 days"),
        ("12m", "12 months"),
    };

    /// <summary>The period of the same length just before a period.</summary>
    /// <param name="period">The period.</param>
    /// <returns>The period before it.</returns>
    public static DashPeriod PreviousOf(DashPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);
        TimeSpan length = period.End - period.Start;
        return new DashPeriod(period.Key + ":prev", "Previous", period.Start - length, period.Start, period.Step);
    }

    /// <summary>
    /// A page-level period: one of <see cref="DashPagePeriods"/>, or a custom one written
    /// "from:yyyy-MM-dd:to:yyyy-MM-dd" (both days included, at most a year back).
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="clock">The viewer's zone.</param>
    /// <param name="now">Now.</param>
    /// <returns>The period (30 days when the key is unknown).</returns>
    public static DashPeriod DashPagePeriodOf(string? key, ZonedClock clock, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (key == "90d")
        {
            return new DashPeriod("90d", "90 days", now.AddDays(-90), now, TimeStep.Week);
        }
        if (key is not null && key.StartsWith("from:", StringComparison.Ordinal))
        {
            string[] parts = key.Split(':');
            DateTime today = clock.Local(now).Date;
            if (parts.Length == 4 && parts[2] == "to"
                && DateTime.TryParseExact(parts[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime from)
                && DateTime.TryParseExact(parts[3], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime to)
                && from <= to && to <= today && from > today.AddYears(-1))
            {
                DateTimeOffset start = AtLocal(clock, from);
                DateTimeOffset end = to == today ? now : AtLocal(clock, to.AddDays(1));
                int days = (int)(to - from).TotalDays + 1;
                TimeStep step = days <= 1 ? TimeStep.Hour : days <= 45 ? TimeStep.Day : days <= 120 ? TimeStep.Week : TimeStep.Month;
                return new DashPeriod(key, "Custom", start, end, step);
            }
        }
        return key is "today" or "7d" or "30d" or "12m" ? DashPeriodOf(key, clock, now) : DashPeriodOf("30d", clock, now);
    }

    /// <summary>
    /// The figures of the six dashboard pages for a mailbox: worked out from its messages' facts
    /// (no bodies) for the period and the one before it, its conversations, and - for the
    /// attachment kinds and the mailing lists - the headers of the newest messages only.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="period">The period.</param>
    /// <param name="clock">The viewer's zone.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The figures.</returns>
    public async Task<DashPages> DashPagesAsync(Guid mailboxId, DashPeriod period, ZonedClock clock, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(clock);
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        if (context is null)
        {
            return new DashPages { Period = period, Previous = PreviousOf(period) };
        }
        MailboxRow box = context.Value.Mailbox;
        DashPeriod previous = PreviousOf(period);
        HashSet<string> ours = (await this.store.ListTenantDomainsAsync(context.Value.Tenant.Id, ct).ConfigureAwait(false))
            .Select(d => d.Domain.ToLowerInvariant()).Append(box.Domain.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

        // One read covers the period, the one before it, the last 14 days and a year for "new senders".
        DateTimeOffset earliest = new[] { previous.Start, now.AddDays(-15), now.AddDays(-366) }.Min();
        IReadOnlyList<MessageFact> all = await this.store.ListMessageFactsAsync(mailboxId, earliest, now.AddMinutes(1), DashFactLimit, ct).ConfigureAwait(false);
        static bool Inbound(MessageFact f) => f.Folder is not ("Sent" or "Drafts" or ScheduledFolder);
        static bool IsJunk(MessageFact f) => f.Folder == Anjal.Mailbox.MailboxSink.JunkFolder;
        List<MessageFact> inPeriod = all.Where(f => f.ReceivedAt >= period.Start && f.ReceivedAt < period.End).ToList();
        List<MessageFact> inPrevious = all.Where(f => f.ReceivedAt >= previous.Start && f.ReceivedAt < previous.End).ToList();
        string SenderOf(MessageFact f) => FirstAddress(f.FromHeader)?.Address.ToLowerInvariant() ?? f.EnvelopeFrom.ToLowerInvariant();
        string NameOf(MessageFact f)
        {
            MailAddress? a = FirstAddress(f.FromHeader);
            return a is { DisplayName.Length: > 0 } ? a.DisplayName : a?.Address ?? f.EnvelopeFrom;
        }
        bool Inside(string address) => address.Contains('@', StringComparison.Ordinal) && ours.Contains(address[(address.IndexOf('@', StringComparison.Ordinal) + 1)..].ToLowerInvariant());

        // The series, step by step, with the period before laid over it.
        var series = new List<SeriesPoint>();
        for (DateTime local = StepStart(period.Start, period.Step, clock); AtLocal(clock, local) < period.End && series.Count < 400; local = Next(local, period.Step))
        {
            DateTimeOffset at = AtLocal(clock, local);
            DateTimeOffset end = AtLocal(clock, Next(local, period.Step));
            TimeSpan offset = period.Start - previous.Start;
            IEnumerable<MessageFact> step = inPeriod.Where(f => f.ReceivedAt >= at && f.ReceivedAt < end);
            long before = inPrevious.LongCount(f => Inbound(f) && !IsJunk(f) && f.ReceivedAt >= at - offset && f.ReceivedAt < end - offset);
            series.Add(new SeriesPoint(at, end, StepLabel(local, period.Step),
                step.LongCount(f => Inbound(f) && !IsJunk(f)), step.LongCount(f => f.Folder == "Sent"), step.LongCount(IsJunk), before));
        }

        // Fourteen daily steps for the key figures' trend lines.
        DateTime today = clock.Local(now).Date;
        var days14 = Enumerable.Range(0, 14).Select(i => AtLocal(clock, today.AddDays(i - 13))).ToList();
        IReadOnlyList<long> Daily(Func<MessageFact, bool> which) =>
            days14.Select(d => all.LongCount(f => which(f) && f.ReceivedAt >= d && f.ReceivedAt < d.AddDays(1))).ToList();

        // Replies: each answer the person sent, and how long after the message it answered.
        IReadOnlyDictionary<string, IReadOnlyList<ConversationItem>> index = await this.ConversationIndexAsync(mailboxId, ct).ConfigureAwait(false);
        var replies = new List<(DateTimeOffset SentAt, TimeSpan Gap, string To)>();
        var answeredIds = new HashSet<Guid>();
        Dictionary<Guid, MessageFact> byId = all.ToDictionary(f => f.Id);
        foreach (IReadOnlyList<ConversationItem> items in index.Values)
        {
            List<ConversationItem> ordered = items.OrderBy(i => i.At).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].Folder is "Sent" or "Drafts" || ordered[i].Folder == Anjal.Mailbox.MailboxSink.JunkFolder)
                {
                    continue;
                }
                ConversationItem? answer = ordered.Skip(i + 1).TakeWhile(o => o.Folder is "Sent" or "Drafts" || o.Folder == Anjal.Mailbox.MailboxSink.JunkFolder).FirstOrDefault(o => o.Folder == "Sent");
                if (answer is not null && answer.At - ordered[i].At <= TimeSpan.FromDays(14))
                {
                    answeredIds.Add(ordered[i].Id);
                    string to = byId.TryGetValue(ordered[i].Id, out MessageFact? got) ? SenderOf(got) : string.Empty;
                    replies.Add((answer.At, answer.At - ordered[i].At, to));
                }
            }
        }
        static TimeSpan? Median(IEnumerable<TimeSpan> gaps)
        {
            List<TimeSpan> g = gaps.OrderBy(x => x).ToList();
            return g.Count == 0 ? null : g.Count % 2 == 1 ? g[g.Count / 2] : (g[(g.Count / 2) - 1] + g[g.Count / 2]) / 2;
        }
        List<(DateTimeOffset SentAt, TimeSpan Gap, string To)> repliesNow = replies.Where(r => r.SentAt >= period.Start && r.SentAt < period.End).ToList();
        long[] bands = new long[4];
        foreach ((DateTimeOffset _, TimeSpan gap, string _) in repliesNow)
        {
            bands[gap <= TimeSpan.FromHours(1) ? 0 : gap <= TimeSpan.FromHours(12) ? 1 : gap <= TimeSpan.FromHours(36) ? 2 : 3]++;
        }

        // Waiting for a reply (the dashboard's own rule, item 63).
        IReadOnlyList<MessageRow> waiting = await this.NoReplyMessagesAsync(mailboxId, now, ct).ConfigureAwait(false);

        // Unread in the Inbox: now, older than a week, and as a backlog by the day it arrived.
        List<MessageFact> unreadInbox = all.Where(f => f.Folder == FolderRow.Inbox && !f.Seen).ToList();

        // Heat map: received by weekday (Monday first) and hour.
        long[,] heat = new long[7, 24];
        foreach (MessageFact f in inPeriod.Where(f => Inbound(f) && !IsJunk(f)))
        {
            DateTime local = clock.Local(f.ReceivedAt).DateTime;
            heat[((int)local.DayOfWeek + 6) % 7, local.Hour]++;
        }

        // Folders and categories.
        List<MessageFact> receivedNow = inPeriod.Where(f => Inbound(f) && !IsJunk(f)).ToList();
        IReadOnlyList<CategoryView> categories = await this.ListCategoriesAsync(mailboxId, ct).ConfigureAwait(false);
        var byFolder = inPeriod.Where(Inbound).GroupBy(f => f.Folder, StringComparer.Ordinal)
            .Select(g => new NamedFigure(g.Key, g.LongCount(), "/folder/" + Uri.EscapeDataString(g.Key), 0, Array.Empty<long>()))
            .OrderByDescending(n => n.Count).ToList();
        var byCategory = receivedNow.GroupBy(f => f.CategoryId)
            .Select(g =>
            {
                CategoryView? c = categories.FirstOrDefault(x => x.Row.Id == g.Key);
                IReadOnlyList<long> trend = series.Select(s => receivedNow.LongCount(f => f.CategoryId == g.Key && f.ReceivedAt >= s.Start && f.ReceivedAt < s.End)).ToList();
                return new NamedFigure(c?.Row.Name ?? Uncategorised, g.LongCount(), c is null ? "/search?scope=all&cat=none" : "/search?scope=all&cat=" + c.Row.Id, c?.Row.Slot ?? 0, trend);
            })
            .OrderByDescending(n => n.Count).ToList();

        // People: both ways, over the period.
        var people = new Dictionary<string, (string Name, long FromThem, long FromYou)>(StringComparer.OrdinalIgnoreCase);
        foreach (MessageFact f in receivedNow)
        {
            string a = SenderOf(f);
            (string Name, long FromThem, long FromYou) p = people.GetValueOrDefault(a, (NameOf(f), 0, 0));
            people[a] = (p.Name, p.FromThem + 1, p.FromYou);
        }
        foreach (MessageFact f in inPeriod.Where(f => f.Folder == "Sent"))
        {
            foreach (MailAddress to in AddressParser.Parse(EncodedWordDecoder.Decode(f.ToHeader)))
            {
                string a = to.Address.ToLowerInvariant();
                (string Name, long FromThem, long FromYou) p = people.GetValueOrDefault(a, (to.DisplayName.Length > 0 ? to.DisplayName : to.Address, 0, 0));
                people[a] = (p.Name, p.FromThem, p.FromYou + 1);
            }
        }
        var traffic = people
            .Where(p => !IsNotifier(p.Key))
            .Select(p =>
            {
                List<MessageFact> theirs = receivedNow.Where(f => string.Equals(SenderOf(f), p.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                int? pct = theirs.Count == 0 ? null : (int)Math.Round(100.0 * theirs.Count(f => answeredIds.Contains(f.Id)) / theirs.Count);
                TimeSpan? usual = Median(repliesNow.Where(r => string.Equals(r.To, p.Key, StringComparison.OrdinalIgnoreCase)).Select(r => r.Gap));
                return new PersonTraffic(p.Key, p.Value.Name, p.Value.FromThem, p.Value.FromYou, pct, usual);
            })
            .OrderByDescending(t => t.FromThem + t.FromYou).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Take(12).ToList();

        // New senders: their first message in the last year came in this period.
        var firstSeen = all.Where(f => Inbound(f) && !IsJunk(f)).GroupBy(SenderOf, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(f => f.ReceivedAt), StringComparer.OrdinalIgnoreCase);
        var newSenders = receivedNow.GroupBy(SenderOf, StringComparer.OrdinalIgnoreCase)
            .Where(g => firstSeen.TryGetValue(g.Key, out DateTimeOffset first) && first >= period.Start && !IsNotifier(g.Key))
            .Select(g => new NamedFigure(NameOf(g.First()), g.LongCount(), "/search?scope=all&sender=" + Uri.EscapeDataString(g.Key), 0, Array.Empty<long>()))
            .OrderByDescending(n => n.Count).Take(10).ToList();
        var byDomain = receivedNow.GroupBy(f => SenderOf(f).Contains('@', StringComparison.Ordinal) ? SenderOf(f)[(SenderOf(f).IndexOf('@', StringComparison.Ordinal) + 1)..] : "?", StringComparer.OrdinalIgnoreCase)
            .Select(g => new NamedFigure(g.Key, g.LongCount(), "/search?scope=all&q=" + Uri.EscapeDataString("@" + g.Key), 0, Array.Empty<long>()))
            .OrderByDescending(n => n.Count).Take(10).ToList();

        // Mailing lists and attachment kinds, from the newest messages' headers.
        (IReadOnlyList<NamedFigure> lists, long listMessages, IReadOnlyList<NamedFigure> kinds, int sample) =
            await this.ListsAndKindsAsync(context.Value.Tenant, box, receivedNow, ct).ConfigureAwait(false);

        // Safety: checks passed, encryption.
        List<MessageFact> checkedNow = receivedNow.Where(f => f.SpamChecked == true).ToList();
        List<MessageFact> known = receivedNow.Where(f => f.TransportEncrypted is not null).ToList();
        var unencrypted = known.Where(f => f.TransportEncrypted == false).GroupBy(SenderOf, StringComparer.OrdinalIgnoreCase)
            .Select(g => new NamedFigure(NameOf(g.First()), g.LongCount(), "/search?scope=all&sender=" + Uri.EscapeDataString(g.Key), 0, Array.Empty<long>()))
            .OrderByDescending(n => n.Count).Take(8).ToList();

        // Storage: by folder, growth by month, the forecast, the biggest.
        IReadOnlyList<(string Folder, long Bytes, long Count)> space = await this.FolderSpaceAsync(mailboxId, ct).ConfigureAwait(false);
        long used = space.Sum(s => s.Bytes);
        var growth = new List<NamedFigure>();
        DateTime monthStart = new(today.Year, today.Month, 1);
        for (int i = 11; i >= 0; i--)
        {
            DateTime m = monthStart.AddMonths(-i);
            DateTimeOffset endOfMonth = AtLocal(clock, m.AddMonths(1));
            long after = all.Where(f => f.ReceivedAt >= endOfMonth).Sum(f => f.SizeBytes);
            growth.Add(new NamedFigure(m.ToString("MMM", CultureInfo.InvariantCulture), Math.Max(0, used - after), null, 0, Array.Empty<long>()));
        }
        long lastThree = all.Where(f => f.ReceivedAt >= now.AddMonths(-3)).Sum(f => f.SizeBytes);
        int? fullIn = box.QuotaBytes > 0 && lastThree > 0 && used < box.QuotaBytes
            ? (int)Math.Ceiling((box.QuotaBytes - used) / (lastThree / 3.0))
            : null;
        var biggest = all.OrderByDescending(f => f.SizeBytes).Take(8).ToList();
        var biggestSenders = all.Where(Inbound).GroupBy(SenderOf, StringComparer.OrdinalIgnoreCase)
            .Select(g => new NamedFigure(NameOf(g.First()), g.Sum(f => f.SizeBytes), "/search?scope=all&sender=" + Uri.EscapeDataString(g.Key), 0, Array.Empty<long>()))
            .OrderByDescending(n => n.Count).Take(8).ToList();

        return new DashPages
        {
            Period = period,
            Previous = previous,
            Received = (receivedNow.Count, inPrevious.LongCount(f => Inbound(f) && !IsJunk(f))),
            Sent = (inPeriod.LongCount(f => f.Folder == "Sent"), inPrevious.LongCount(f => f.Folder == "Sent")),
            Junk = (inPeriod.LongCount(IsJunk), inPrevious.LongCount(IsJunk)),
            Unread = (unreadInbox.Count, unreadInbox.LongCount(f => f.ReceivedAt < now.AddDays(-7))),
            Waiting = (waiting.Count, waiting.LongCount(m => m.ReceivedAt < now.AddDays(-6))),
            UsualReply = (Median(repliesNow.Select(r => r.Gap)), Median(replies.Where(r => r.SentAt >= previous.Start && r.SentAt < previous.End).Select(r => r.Gap))),
            Series = series,
            Trends = new Dictionary<string, IReadOnlyList<long>>(StringComparer.Ordinal)
            {
                ["received"] = Daily(f => Inbound(f) && !IsJunk(f)),
                ["sent"] = Daily(f => f.Folder == "Sent"),
                ["unread"] = Daily(f => f.Folder == FolderRow.Inbox && !f.Seen),
                ["reply"] = days14.Select(d => (long)(Median(replies.Where(r => r.SentAt >= d && r.SentAt < d.AddDays(1)).Select(r => r.Gap))?.TotalMinutes ?? 0)).ToList(),
                ["waiting"] = days14.Select(d => waiting.LongCount(m => m.ReceivedAt < d.AddDays(1) && m.ReceivedAt >= d.AddDays(-27))).ToList(),
                ["junk"] = Daily(IsJunk),
            },
            Heat = heat,
            InsideOutside = (receivedNow.LongCount(f => Inside(SenderOf(f))), receivedNow.LongCount(f => !Inside(SenderOf(f)))),
            ByFolder = byFolder,
            ByCategory = byCategory,
            AttachmentKinds = kinds,
            AttachmentSample = sample,
            Lists = lists,
            ListMessages = listMessages,
            People = traffic,
            NewSenders = newSenders,
            ByDomain = byDomain,
            ReplyBands = bands,
            ReplyInsideOutside = (Median(repliesNow.Where(r => Inside(r.To)).Select(r => r.Gap)), Median(repliesNow.Where(r => !Inside(r.To)).Select(r => r.Gap))),
            NotAnswered = receivedNow.Where(f => f.Folder == FolderRow.Inbox && !f.Answered && !answeredIds.Contains(f.Id) && f.ReceivedAt < now.AddDays(-2)
                    && !IsNotifier(SenderOf(f)) && AddressParser.Parse(EncodedWordDecoder.Decode(f.ToHeader)).Any(a => string.Equals(a.Address, box.Address, StringComparison.OrdinalIgnoreCase)))
                .Take(50).ToList(),
            UnreadBacklog = series.Select(s => s with { Received = unreadInbox.LongCount(f => f.ReceivedAt >= s.Start && f.ReceivedAt < s.End), Sent = 0, Junk = 0, Previous = 0 }).ToList(),
            Verified = (checkedNow.LongCount(f => f.SpamScore == 0), checkedNow.Count),
            Encrypted = (known.LongCount(f => f.TransportEncrypted == true), known.Count),
            UnencryptedSenders = unencrypted,
            Space = space.Select(s => new NamedFigure(s.Folder, s.Bytes, "/folder/" + Uri.EscapeDataString(s.Folder), 0, Array.Empty<long>())).ToList(),
            Storage = (used, box.QuotaBytes),
            Growth = growth,
            FullInMonths = fullIn,
            Biggest = biggest,
            BiggestSenders = biggestSenders,
        };
    }

    // The first step of a period, in the viewer's clock: the hour, day, Monday or first of the month it starts in.
    private static DateTime StepStart(DateTimeOffset start, TimeStep step, ZonedClock clock)
    {
        DateTime local = clock.Local(start).DateTime;
        return step switch
        {
            TimeStep.Hour => local.Date.AddHours(local.Hour),
            TimeStep.Day => local.Date,
            TimeStep.Week => local.Date.AddDays(-(((int)local.DayOfWeek + 6) % 7)),
            _ => new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified),
        };
    }

    private static string StepLabel(DateTime local, TimeStep step) => step switch
    {
        TimeStep.Hour => local.ToString("HH", CultureInfo.InvariantCulture),
        TimeStep.Month => local.ToString("MMM", CultureInfo.InvariantCulture),
        _ => local.ToString("d MMM", CultureInfo.InvariantCulture),
    };

    // The mailing lists (List-Id, or a notifying sender) and the kinds of attachment, read from the
    // headers of the newest received messages only, so a big mailbox stays quick.
    private async Task<(IReadOnlyList<NamedFigure> Lists, long ListMessages, IReadOnlyList<NamedFigure> Kinds, int Sample)> ListsAndKindsAsync(
        TenantRow tenant, MailboxRow box, IReadOnlyList<MessageFact> received, CancellationToken ct)
    {
        var lists = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long listMessages = 0;
        var kinds = new Dictionary<string, long>(StringComparer.Ordinal);
        int sample = 0;
        IReadOnlyList<FolderView> folders = await this.ListFoldersAsync(box.Id, ct).ConfigureAwait(false);
        foreach (MessageFact f in received.Take(AttachmentSampleLimit * 2))
        {
            string sender = FirstAddress(f.FromHeader)?.Address ?? f.EnvelopeFrom;
            bool wantsHeaders = f.HasAttachments && sample < AttachmentSampleLimit;
            string? listName = IsNotifier(sender) ? sender : null;
            if (!wantsHeaders && listName is null && listMessages >= AttachmentSampleLimit)
            {
                continue;
            }
            MessageRow? row = await this.store.GetMessageByIdAsync(f.Id, ct).ConfigureAwait(false);
            byte[]? raw = row is null ? null : await this.maildir.ReadAsync(tenant.Slug, box.Address, f.Folder, row.MaildirFile, ct).ConfigureAwait(false);
            if (raw is null)
            {
                continue;
            }
            string head = System.Text.Encoding.UTF8.GetString(raw, 0, HeaderEnd(raw));
            if (listName is null && HeadersSayList(head))
            {
                listName = ListName(head) ?? sender;
            }
            if (listName is not null)
            {
                lists[listName] = lists.GetValueOrDefault(listName) + 1;
                listMessages++;
            }
            if (wantsHeaders)
            {
                sample++;
                foreach ((string name, string type) in AttachmentNames(raw))
                {
                    (string tag, string kind) = FileTag.Of(name, type);
                    string key = kind == "other" ? "Other" : kind == "img" ? "JPG / PNG" : tag;
                    kinds[key] = kinds.GetValueOrDefault(key) + 1;
                }
            }
        }
        _ = folders;
        return (
            lists.OrderByDescending(l => l.Value).Take(8).Select(l => new NamedFigure(l.Key, l.Value, "/search?scope=all&q=" + Uri.EscapeDataString(l.Key), 0, Array.Empty<long>())).ToList(),
            listMessages,
            kinds.OrderByDescending(k => k.Value).Select(k => new NamedFigure(k.Key, k.Value, "/search?scope=all&attach=1", 0, Array.Empty<long>())).ToList(),
            sample);
    }

    // The list's name from its List-Id header ("Medline news <news.medline.example>" gives "Medline news").
    private static string? ListName(string head)
    {
        foreach (string line in head.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("List-Id:", StringComparison.OrdinalIgnoreCase))
            {
                string v = EncodedWordDecoder.Decode(line[8..].Trim());
                int lt = v.IndexOf('<', StringComparison.Ordinal);
                string name = (lt > 0 ? v[..lt] : v).Trim().Trim('"');
                return name.Length > 0 ? name : v.Trim('<', '>', ' ');
            }
        }
        return null;
    }

    // The names and types of a message's attachments, as the reader lists them.
    /// <summary>The attachments' names on each of some messages (rc.22, item 47): for the file tags on a search page.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageIds">The messages (a page at most).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Names by message, for those with any.</returns>
    public async Task<Dictionary<Guid, IReadOnlyList<string>>> AttachmentNamesForAsync(Guid mailboxId, IReadOnlyList<Guid> messageIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        var result = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (Guid id in messageIds.Take(50))
        {
            (MessageRow Row, FolderRow Folder, byte[] Raw)? found = await this.ReadRawAsync(mailboxId, id, ct).ConfigureAwait(false);
            if (found is null)
            {
                continue;
            }
            List<(string Name, string Type)> names = AttachmentNames(found.Value.Raw);
            if (names.Count > 0)
            {
                result[id] = names.Select(n => n.Name).ToList();
            }
        }
        return result;
    }

    private static List<(string Name, string Type)> AttachmentNames(byte[] raw)
    {
        var found = new List<(string Name, string Type)>();
        if (TryParse(raw) is not MimeMessage parsed)
        {
            return found;
        }
        MimePart? html = null;
        MimePart? text = null;
        var attachments = new List<(MimePart Part, AttachmentView View)>();
        Walk(parsed.Body, ref html, ref text, attachments);
        foreach ((MimePart _, AttachmentView view) in attachments)
        {
            found.Add((view.FileName, view.ContentType));
        }
        return found;
    }

    // The first address of a header, or null (CA1826: no Enumerable on a list).
    private static MailAddress? FirstAddress(string header)
    {
        IReadOnlyList<MailAddress> parsed = AddressParser.Parse(EncodedWordDecoder.Decode(header));
        return parsed.Count > 0 ? parsed[0] : null;
    }
}
