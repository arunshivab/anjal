using System.Globalization;
using System.Text;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>One message sent by one of the organisation's applications (rc.17, design review [7b]).</summary>
/// <param name="Id">The queue's id.</param>
/// <param name="Status">Delivered, Delayed, Queued, Bounced, Failed or Suppressed.</param>
/// <param name="At">When the application handed it over.</param>
/// <param name="To">The recipient.</param>
/// <param name="Subject">Its subject.</param>
/// <param name="App">The application's name.</param>
/// <param name="AppId">The application's id.</param>
/// <param name="Tries">How many times sending was tried.</param>
/// <param name="Next">The next try, while it is still being tried.</param>
/// <param name="GiveUp">When Anjal gives up.</param>
/// <param name="LastError">The receiving server's last reply, or why it stopped.</param>
/// <param name="Took">How long delivery took, once delivered.</param>
public sealed record SendingRow(Guid Id, string Status, DateTimeOffset At, string To, string Subject, string App, string AppId, int Tries, DateTimeOffset? Next, DateTimeOffset GiveUp, string LastError, TimeSpan? Took);

/// <summary>One application's figures for a period.</summary>
/// <param name="App">The application.</param>
/// <param name="Accepted">Handed over.</param>
/// <param name="Delivered">Accepted by the receiving server.</param>
/// <param name="Delayed">Still being tried.</param>
/// <param name="Bounced">Refused for good.</param>
/// <param name="Failed">Given up on, or stopped.</param>
/// <param name="Usual">The usual (median) time to deliver.</param>
/// <param name="Health">"Healthy", or what needs doing.</param>
public sealed record SendingAppFigures(OrgApp App, long Accepted, long Delivered, long Delayed, long Bounced, long Failed, TimeSpan? Usual, string Health);

/// <summary>Something on the Sending systems overview that needs the administrator.</summary>
/// <param name="Icon">The LiPicons name.</param>
/// <param name="Title">What it is (words from the word list, with {placeholders}).</param>
/// <param name="Text">The detail (likewise).</param>
/// <param name="Values">The placeholders' values; "date" is a time in ISO 8601, shown in the viewer's zone.</param>
/// <param name="Action">The button's words.</param>
/// <param name="Href">Where it goes.</param>
/// <param name="Tone">warn, bad or ok.</param>
public sealed record SendingAttention(string Icon, string Title, string Text, IReadOnlyDictionary<string, string> Values, string Action, string Href, string Tone);

/// <summary>A period's counts for one step of the chart.</summary>
/// <param name="Start">The step's start.</param>
/// <param name="Delivered">Delivered.</param>
/// <param name="Delayed">Still being tried.</param>
/// <param name="Bounced">Refused for good.</param>
/// <param name="Failed">Given up on.</param>
public sealed record SendingStep(DateTimeOffset Start, int Delivered, int Delayed, int Bounced, int Failed);

/// <summary>The Sending systems overview for a period (design review [7a]).</summary>
/// <param name="Period">The period's key: 1h, 24h, 7d or 30d.</param>
/// <param name="Start">From.</param>
/// <param name="End">Until.</param>
/// <param name="Rows">Every message in the period (newest first, at most 20,000).</param>
/// <param name="Steps">The chart, oldest first.</param>
/// <param name="Apps">Each application's figures.</param>
/// <param name="Attention">What needs the administrator.</param>
/// <param name="ListAdded">Addresses added to the do-not-send list in the period.</param>
public sealed record SendingOverview(string Period, DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<SendingRow> Rows, IReadOnlyList<SendingStep> Steps, IReadOnlyList<SendingAppFigures> Apps, IReadOnlyList<SendingAttention> Attention, int ListAdded)
{
    /// <summary>How many messages have this status.</summary>
    /// <param name="status">The status.</param>
    /// <returns>The count.</returns>
    public int Count(string status) => this.Rows.Count(r => r.Status == status);

    /// <summary>The usual (median) delivery time.</summary>
    public TimeSpan? Usual => MailboxService.Median(this.Rows.Where(r => r.Took is not null).Select(r => r.Took!.Value).ToList());
}

/// <summary>One delivery attempt in a message's timeline (design review [7b]).</summary>
/// <param name="At">When it ended.</param>
/// <param name="Host">The receiving server.</param>
/// <param name="Tls">The TLS version and cipher, or null.</param>
/// <param name="Code">The reply code.</param>
/// <param name="Reply">The receiving server's exact reply.</param>
/// <param name="Outcome">delivered, deferred or refused.</param>
public sealed record SendingAttempt(DateTimeOffset At, string Host, string? Tls, int? Code, string Reply, string Outcome);

/// <summary>Everything about one message (design review [7b], the detail panel).</summary>
/// <param name="Row">The message.</param>
/// <param name="MessageId">Its Message-ID.</param>
/// <param name="Sha256">The fingerprint of the original kept, or null when none is kept.</param>
/// <param name="Attempts">Each attempt, oldest first.</param>
/// <param name="ContentRemoved">When the message's content was removed after the organisation's keep-days (rc.21.1, DEF-117), or null while it is kept.</param>
public sealed record SendingDetail(SendingRow Row, string MessageId, string? Sha256, IReadOnlyList<SendingAttempt> Attempts, DateTimeOffset? ContentRemoved = null);

/// <summary>One notification to an application's webhook (design review [7d]).</summary>
/// <param name="Job">The notification.</param>
/// <param name="Code">The last HTTP status the webhook answered, or null.</param>
/// <param name="Url">The webhook it went to.</param>
/// <param name="Deliveries">Each recorded attempt, newest first.</param>
public sealed record WebhookView(WebhookJob Job, int? Code, string Url, IReadOnlyList<WebhookDelivery> Deliveries);

/// <summary>A receiving server's domain that slows delivery down.</summary>
/// <param name="Domain">The domain.</param>
/// <param name="Waiting">Messages still being tried.</param>
/// <param name="Failed">Messages given up on or refused.</param>
/// <param name="Reply">The latest reply from it, when known.</param>
public sealed record SlowReceiver(string Domain, long Waiting, long Failed, string Reply);

/// <summary>The Anjal console's Transactional page (design review [7e]).</summary>
/// <param name="Waiting">Messages in the queue now.</param>
/// <param name="Oldest">How long the oldest has waited.</param>
/// <param name="Accepted">Messages from applications since the start of the day.</param>
/// <param name="Organisations">Organisations that have applications.</param>
/// <param name="Delivered">Delivered.</param>
/// <param name="FirstTry">Delivered on the first try.</param>
/// <param name="Slow">The receiving servers that slow delivery down.</param>
public sealed record TransactionalView(long Waiting, TimeSpan? Oldest, int Accepted, int Organisations, int Delivered, int FirstTry, IReadOnlyList<SlowReceiver> Slow);

/// <summary>
/// rc.17 (design review section 10, owner 11 Oct 2026): Sending systems - what the organisation's
/// applications sent, whether it arrived, and why not; their webhooks; the do-not-send list.
/// Worked out from the queue, the evidence store and the webhook queue; nothing new is recorded.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The periods the overview offers.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> SendingPeriods = new[] { ("1h", "1 hour"), ("24h", "24 hours"), ("7d", "7 days"), ("30d", "30 days") };

    private const int SendingRowLimit = 20000;

    /// <summary>The start of a Sending systems period.</summary>
    /// <param name="key">1h, 24h, 7d or 30d.</param>
    /// <param name="now">Now.</param>
    /// <returns>The period's start and its key (24h when the key is unknown).</returns>
    public static (DateTimeOffset Start, string Key) SendingPeriodOf(string? key, DateTimeOffset now) => key switch
    {
        "1h" => (now.AddHours(-1), "1h"),
        "7d" => (now.AddDays(-7), "7d"),
        "30d" => (now.AddDays(-30), "30d"),
        _ => (now.AddHours(-24), "24h"),
    };

    /// <summary>A message's status in words the screen uses.</summary>
    /// <param name="m">The queued message.</param>
    /// <returns>Delivered, Delayed, Queued, Bounced, Failed or Suppressed.</returns>
    public static string SendingStatus(OutboundMessage m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return m.Status switch
        {
            OutboundStatus.Sent => "Delivered",
            OutboundStatus.Pending or OutboundStatus.Sending => m.Attempts > 0 ? "Delayed" : "Queued",
            _ when m.LastError.StartsWith(DoNotSend.SuppressedPrefix, StringComparison.Ordinal) => "Suppressed",
            _ when m.LastError.StartsWith("Give-up reached", StringComparison.Ordinal)
                || m.LastError == OutboundMessage.CancelledBySender
                || m.LastError.StartsWith("DKIM", StringComparison.Ordinal)
                || m.LastError.StartsWith("Not sent", StringComparison.Ordinal) => "Failed",
            _ => "Bounced",
        };
    }

    /// <summary>The middle value of some durations, or null when there are none.</summary>
    /// <param name="values">The durations.</param>
    /// <returns>The median.</returns>
    public static TimeSpan? Median(List<TimeSpan> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return null;
        }
        values.Sort();
        return values[values.Count / 2];
    }

    /// <summary>The organisation's messages from its applications in a period, newest first.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="start">From.</param>
    /// <param name="end">Until.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rows.</returns>
    public async Task<IReadOnlyList<SendingRow>> SendingRowsAsync(TenantRow tenant, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        List<OrgApp> apps = await this.AppsOfAsync(tenant.Id, ct).ConfigureAwait(false);
        var bySender = new Dictionary<string, OrgApp>(StringComparer.OrdinalIgnoreCase);
        foreach (OrgApp a in apps.Where(a => a.SendsAs.Length > 0))
        {
            bySender.TryAdd(a.SendsAs.Trim(), a);
        }
        if (bySender.Count == 0)
        {
            return Array.Empty<SendingRow>();
        }
        IReadOnlyList<OutboundMessage> found = await this.messageStore.ListOutboundBySendersAsync(bySender.Keys.ToList(), start, end, SendingRowLimit, ct).ConfigureAwait(false);
        return found.Select(m => ToSendingRow(m, bySender.TryGetValue(m.EnvelopeFrom.Trim(), out OrgApp? app) ? app : null)).ToList();
    }

    private static SendingRow ToSendingRow(OutboundMessage m, OrgApp? app)
    {
        string status = SendingStatus(m);
        return new SendingRow(m.Id, status, m.CreatedAt, m.EnvelopeTo, HeaderOf(m.RawBytes, "Subject"), app?.Name ?? m.EnvelopeFrom, app?.Id ?? string.Empty,
            m.Attempts, status is "Delayed" or "Queued" ? m.NextAttemptAt : null, m.GiveUpAt, m.LastError,
            status == "Delivered" && m.NextAttemptAt >= m.CreatedAt ? m.NextAttemptAt - m.CreatedAt : null);
    }

    /// <summary>The Sending systems overview (design review [7a]).</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="periodKey">1h, 24h, 7d or 30d.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The figures.</returns>
    public async Task<SendingOverview> SendingOverviewAsync(TenantRow tenant, string? periodKey, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        (DateTimeOffset start, string key) = SendingPeriodOf(periodKey, now);
        IReadOnlyList<SendingRow> rows = await this.SendingRowsAsync(tenant, start, now, ct).ConfigureAwait(false);
        List<OrgApp> apps = await this.AppsOfAsync(tenant.Id, ct).ConfigureAwait(false);

        // The chart: 12 five-minute steps for an hour, hours for a day, days for longer.
        TimeSpan step = key switch { "1h" => TimeSpan.FromMinutes(5), "24h" => TimeSpan.FromHours(1), _ => TimeSpan.FromDays(1) };
        int count = (int)Math.Ceiling((now - start) / step);
        var steps = new List<SendingStep>();
        for (int i = 0; i < count; i++)
        {
            DateTimeOffset s = start + (step * i);
            DateTimeOffset e = s + step;
            List<SendingRow> inStep = rows.Where(r => r.At >= s && r.At < e).ToList();
            steps.Add(new SendingStep(s, inStep.Count(r => r.Status == "Delivered"), inStep.Count(r => r.Status is "Delayed" or "Queued"),
                inStep.Count(r => r.Status == "Bounced"), inStep.Count(r => r.Status is "Failed" or "Suppressed")));
        }

        var figures = new List<SendingAppFigures>();
        foreach (OrgApp a in apps)
        {
            List<SendingRow> mine = rows.Where(r => r.AppId == a.Id).ToList();
            string health = a.Keys.Count == 0 ? "No key"
                : a.Keys.FirstOrDefault(k => k.EndsAt is DateTimeOffset end && end - now < TimeSpan.FromDays(14)) is AppKey ending ? "Key ends soon"
                : mine.Count(r => r.Status == "Bounced") > Math.Max(5, mine.Count / 20) ? "Many bounces"
                : "Healthy";
            figures.Add(new SendingAppFigures(a, mine.Count, mine.Count(r => r.Status == "Delivered"), mine.Count(r => r.Status is "Delayed" or "Queued"),
                mine.Count(r => r.Status == "Bounced"), mine.Count(r => r.Status is "Failed" or "Suppressed"),
                Median(mine.Where(r => r.Took is not null).Select(r => r.Took!.Value).ToList()), health));
        }

        var attention = new List<SendingAttention>();
        foreach (IGrouping<string, SendingRow> slow in rows.Where(r => r.Status == "Delayed").GroupBy(r => DomainOf(r.To)).Where(g => g.Count() >= 3).OrderByDescending(g => g.Count()).Take(2))
        {
            attention.Add(new SendingAttention("clock", "{domain} is slow", "{n} messages delayed, still being tried",
                new Dictionary<string, string> { ["domain"] = slow.Key, ["n"] = slow.Count().ToString(CultureInfo.InvariantCulture) }, "See them", "/org/apps?tab=messages&status=Delayed&q=" + Uri.EscapeDataString("@" + slow.Key), "warn"));
        }
        IReadOnlyList<string> domains = (await this.store.ListTenantDomainsAsync(tenant.Id, ct).ConfigureAwait(false)).Select(d => d.Domain).ToList();
        IReadOnlyList<WebhookJob> hooks = await this.messageStore.ListWebhookJobsAsync(domains, start, 500, ct).ConfigureAwait(false);
        int hookFailed = hooks.Count(h => h.Status == WebhookJobStatus.Failed);
        if (hookFailed > 0)
        {
            attention.Add(new SendingAttention("webhook", "{n} webhook deliveries failed", "The application's webhook did not answer; replay them once it is back.",
                new Dictionary<string, string> { ["n"] = hookFailed.ToString(CultureInfo.InvariantCulture) }, "Replay", "/org/apps?tab=webhooks", "bad"));
        }
        foreach (OrgApp a in apps)
        {
            if (a.Keys.FirstOrDefault(k => k.EndsAt is DateTimeOffset end && end > now && end - now < TimeSpan.FromDays(14)) is AppKey ending)
            {
                attention.Add(new SendingAttention("lock", "{app} key ends {date}", "Make a new key and switch the application over.",
                    new Dictionary<string, string> { ["app"] = a.Name, ["date"] = ending.EndsAt!.Value.ToString("o", CultureInfo.InvariantCulture) }, "Rotate", "/org/apps?sel=" + Uri.EscapeDataString(a.Id), "warn"));
            }
        }
        if (await this.DnsCheckOfAsync(tenant.Id, ct).ConfigureAwait(false) is DnsCheckRecord check)
        {
            bool allOk = check.Domains.Count > 0 && check.Domains.Values.All(r => r.Where(kv => kv.Key is "SPF" or "DKIM" or "DMARC").All(kv => kv.Value is not ("missing" or "different")));
            attention.Add(allOk
                ? new SendingAttention("check-circle", "DNS is correct", "SPF, DKIM and DMARC checked {date}", new Dictionary<string, string> { ["date"] = check.At.ToString("o", CultureInfo.InvariantCulture) }, "Details", "/org/apps?tab=dns", "ok")
                : new SendingAttention("warning", "DNS needs attention", "A record is missing or differs; receiving servers may refuse your mail.", new Dictionary<string, string>(), "Details", "/org/apps?tab=dns", "bad"));
        }
        DoNotSendDocument list = await DoNotSend.ReadAsync(this.store, tenant.Id, ct).ConfigureAwait(false);
        return new SendingOverview(key, start, now, rows, steps, figures, attention, list.Entries.Count(e => e.Added >= start));
    }

    /// <summary>One message, with every attempt (design review [7b]); null when it is not one of the organisation's.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="id">The queue's id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The message and its timeline, or null.</returns>
    public async Task<SendingDetail?> SendingDetailAsync(TenantRow tenant, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (await this.OwnOutboundAsync(tenant, id, ct).ConfigureAwait(false) is not (OutboundMessage m, OrgApp app))
        {
            return null;
        }
        string? sha = null;
        var attempts = new List<SendingAttempt>();
        if (this.messageStore is IEvidenceStore evidence)
        {
            (Guid? evidenceId, _) = await evidence.GetOutboundEvidenceLinkAsync(id, ct).ConfigureAwait(false);
            if (evidenceId is Guid e)
            {
                sha = (await evidence.GetEvidenceAsync(e, ct).ConfigureAwait(false))?.Sha256;
                attempts.AddRange((await evidence.ListEvidenceAttemptsAsync(e, ct).ConfigureAwait(false))
                    .OrderBy(a => a.AttemptedAt)
                    .Select(a => new SendingAttempt(a.AttemptedAt, a.RemoteHost, a.TransportTls, a.ReplyCode, a.ReplyText, a.Outcome)));
            }
        }
        return new SendingDetail(ToSendingRow(m, app), HeaderOf(m.RawBytes, "Message-ID").Trim('<', '>', ' '), sha, attempts, m.BodyRemovedAt);
    }

    /// <summary>The original of one of the organisation's messages, as an .eml file; null when it is not theirs.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="id">The queue's id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The bytes, or null.</returns>
    public async Task<byte[]?> SendingOriginalAsync(TenantRow tenant, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        // rc.21.1 (DEF-117): once the content is removed there is no original here to give; the
        // untouched original is in the evidence store, for legal use only.
        return await this.OwnOutboundAsync(tenant, id, ct).ConfigureAwait(false) is (OutboundMessage m, _) && m.BodyRemovedAt is null ? m.RawBytes : null;
    }

    /// <summary>Anjal's outer limit for keeping a delivered message's content in the queue (policy CMP-02).</summary>
    public const int MaxOutboxDays = 30;

    /// <summary>
    /// v1.0.0-rc.21.1 (DEF-117, owner 16 Oct 2026, option (a)): remove the content of messages
    /// whose delivery ended longer ago than their organisation keeps Outbox copies (Organisation
    /// &gt; Retention, 1 to 30 days, default 7), keeping the header block so the record of who,
    /// when and what happened stays. Mail from a mailbox under a legal hold is kept whole. Mail
    /// from addresses no organisation owns (Anjal's own system mail) is cleared after Anjal's 30
    /// days. The evidence store is not touched.
    /// </summary>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many messages had their content removed.</returns>
    public async Task<int> ClearOldOutboxAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        const int batch = 500;
        int removed = 0;
        var held = new List<string>();
        var plans = new List<(IReadOnlyList<string> Domains, int Days)>();
        foreach (TenantRow t in await this.store.ListTenantsAsync(ct).ConfigureAwait(false))
        {
            RetentionPolicy p = await this.RetentionOfAsync(t, ct).ConfigureAwait(false);
            IReadOnlyList<string> domains = (await this.store.ListTenantDomainsAsync(t.Id, ct).ConfigureAwait(false)).Select(d => d.Domain).ToList();
            plans.Add((domains, Math.Clamp(p.OutboxDays, 1, MaxOutboxDays)));
            foreach (LegalHold h in await this.ReadTenantDocumentAsync<List<LegalHold>>(t.Id, HoldsKind, ct).ConfigureAwait(false) ?? new List<LegalHold>())
            {
                if (await this.store.GetMailboxByIdAsync(h.MailboxId, ct).ConfigureAwait(false) is MailboxRow m)
                {
                    held.Add(m.Address);
                }
            }
        }
        foreach ((IReadOnlyList<string> domains, int days) in plans.Where(p => p.Domains.Count > 0))
        {
            removed += await RemoveAllAsync(domains, days).ConfigureAwait(false);
        }
        // Whatever is left - addresses no organisation owns - after Anjal's outer limit.
        removed += await RemoveAllAsync(Array.Empty<string>(), MaxOutboxDays).ConfigureAwait(false);
        return removed;

        async Task<int> RemoveAllAsync(IReadOnlyList<string> domains, int days)
        {
            int total = 0;
            for (int i = 0; i < 200; i++)
            {
                int n = await this.messageStore.RemoveOutboundContentAsync(domains, held, now - TimeSpan.FromDays(days), batch, ct).ConfigureAwait(false);
                total += n;
                if (n < batch)
                {
                    break;
                }
            }
            return total;
        }
    }

    /// <summary>Try one of the organisation's waiting messages now, or stop trying it.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="id">The queue's id.</param>
    /// <param name="stop">True to stop trying; false to try now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when it was waiting and is now changed.</returns>
    public async Task<bool> SendingRetryOrStopAsync(TenantRow tenant, Guid id, bool stop, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (await this.OwnOutboundAsync(tenant, id, ct).ConfigureAwait(false) is not (OutboundMessage m, _))
        {
            return false;
        }
        return stop
            ? await this.messageStore.CancelOutboundAsync(id, m.EnvelopeFrom, ct).ConfigureAwait(false)
            : await this.messageStore.RetryOutboundNowAsync(id, m.EnvelopeFrom, ct).ConfigureAwait(false);
    }

    // A queued message, only when one of this organisation's applications sent it.
    private async Task<(OutboundMessage Message, OrgApp App)?> OwnOutboundAsync(TenantRow tenant, Guid id, CancellationToken ct)
    {
        OutboundMessage? m = await this.messageStore.GetOutboundByIdAsync(id, ct).ConfigureAwait(false);
        if (m is null)
        {
            return null;
        }
        OrgApp? app = (await this.AppsOfAsync(tenant.Id, ct).ConfigureAwait(false)).FirstOrDefault(a => string.Equals(a.SendsAs.Trim(), m.EnvelopeFrom.Trim(), StringComparison.OrdinalIgnoreCase));
        return app is null ? null : (m, app);
    }

    /// <summary>
    /// Send a short test message from an application's address to the administrator, through the
    /// same queue the application uses (design review [7c], Send a test).
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="appId">The application.</param>
    /// <param name="to">Who gets the test.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The queue's id, or null when there is no such application.</returns>
    public async Task<Guid?> SendingTestAsync(TenantRow tenant, string appId, string to, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(appId);
        ArgumentNullException.ThrowIfNull(to);
        OrgApp? app = (await this.AppsOfAsync(tenant.Id, ct).ConfigureAwait(false)).FirstOrDefault(a => a.Id == appId);
        if (app is null || app.SendsAs.Length == 0)
        {
            return null;
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string domain = app.SendsAs[(app.SendsAs.LastIndexOf('@') + 1)..];
        var sb = new StringBuilder();
        sb.Append("From: \"").Append(app.Name.Replace("\"", string.Empty, StringComparison.Ordinal)).Append("\" <").Append(app.SendsAs).Append(">\r\n");
        sb.Append("To: <").Append(to).Append(">\r\n");
        sb.Append("Subject: ").Append(EncodeHeaderText("Test from " + app.Name)).Append("\r\n");
        sb.Append("Date: ").Append(FormatDate(now)).Append("\r\n");
        sb.Append("Message-ID: <").Append(Guid.NewGuid().ToString("N")).Append('@').Append(domain).Append(">\r\n");
        sb.Append("Auto-Submitted: auto-generated\r\n");
        sb.Append("MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\n");
        sb.Append("A test sent through Anjal's queue as ").Append(app.SendsAs).Append(", the way ").Append(app.Name).Append(" sends. If it arrived, the address, its DKIM key and delivery work.\r\n");
        OutboundMessage queued = await this.messageStore.EnqueueOutboundAsync(new OutboundMessage
        {
            EnvelopeFrom = app.SendsAs,
            EnvelopeTo = to,
            RawBytes = Encoding.UTF8.GetBytes(sb.ToString()),
            CreatedAt = now,
            NextAttemptAt = now,
            GiveUpAt = now + TimeSpan.FromHours(1),
        }, ct).ConfigureAwait(false);
        return queued.Id;
    }

    /// <summary>Notifications to the organisation's webhooks in a period, newest first (design review [7d]).</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="since">From.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The notifications with their last answer.</returns>
    public async Task<IReadOnlyList<WebhookView>> WebhooksAsync(TenantRow tenant, DateTimeOffset since, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        IReadOnlyList<string> domains = (await this.store.ListTenantDomainsAsync(tenant.Id, ct).ConfigureAwait(false)).Select(d => d.Domain).ToList();
        IReadOnlyList<WebhookJob> jobs = await this.messageStore.ListWebhookJobsAsync(domains, since, 200, ct).ConfigureAwait(false);
        IReadOnlyList<WebhookDelivery> tries = await this.messageStore.ListWebhookDeliveriesAsync(jobs.Select(j => j.InboundMessageId).ToList(), ct).ConfigureAwait(false);
        ILookup<Guid, WebhookDelivery> byMessage = tries.ToLookup(d => d.InboundMessageId);
        return jobs.Select(j =>
        {
            List<WebhookDelivery> mine = byMessage[j.InboundMessageId].ToList();
            WebhookDelivery? last = mine.FirstOrDefault();
            return new WebhookView(j, last is null || last.StatusCode == 0 ? null : last.StatusCode, last?.Url ?? string.Empty, mine);
        }).ToList();
    }

    /// <summary>Post one of the organisation's notifications again (design review [7d], Replay).</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="id">The notification.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when it waits to be posted again.</returns>
    public async Task<bool> ReplayWebhookAsync(TenantRow tenant, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        IReadOnlyList<WebhookView> recent = await this.WebhooksAsync(tenant, DateTimeOffset.UtcNow.AddDays(-30), ct).ConfigureAwait(false);
        if (!recent.Any(v => v.Job.Id == id))
        {
            return false;
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return await this.messageStore.ReplayWebhookJobAsync(id, now, now + TimeSpan.FromHours(24), ct).ConfigureAwait(false);
    }

    /// <summary>The organisation's do-not-send list, newest first.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The entries.</returns>
    public async Task<IReadOnlyList<DoNotSendEntry>> DoNotSendListAsync(TenantRow tenant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return (await DoNotSend.ReadAsync(this.store, tenant.Id, ct).ConfigureAwait(false)).Entries.OrderByDescending(e => e.Added).ToList();
    }

    /// <summary>Add an address to the organisation's do-not-send list, or take one off. Returns the error, or null.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="address">The address.</param>
    /// <param name="reason">Why (when adding).</param>
    /// <param name="by">The administrator.</param>
    /// <param name="remove">True to take it off.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> SetDoNotSendAsync(TenantRow tenant, string address, string reason, string by, bool remove, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(by);
        string a = address.Trim().ToLowerInvariant();
        DoNotSendDocument doc = await DoNotSend.ReadAsync(this.store, tenant.Id, ct).ConfigureAwait(false);
        if (remove)
        {
            doc.Entries.RemoveAll(e => string.Equals(e.Address, a, StringComparison.OrdinalIgnoreCase));
            doc.GaveUp.Remove(a);
        }
        else
        {
            if (a.Length > 254 || a.IndexOf('@', StringComparison.Ordinal) <= 0 || a.EndsWith('@') || a.Contains(' ', StringComparison.Ordinal))
            {
                return "Type one whole address, such as name@example.in.";
            }
            if (doc.Entries.Exists(e => string.Equals(e.Address, a, StringComparison.OrdinalIgnoreCase)))
            {
                return "That address is already on the list.";
            }
            doc.Entries.Add(new DoNotSendEntry { Address = a, Reason = reason.Trim().Length > 0 ? reason.Trim()[..Math.Min(100, reason.Trim().Length)] : "Added by an administrator", Added = DateTimeOffset.UtcNow, By = by });
        }
        await DoNotSend.WriteAsync(this.store, tenant.Id, doc, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// The Anjal console's Transactional page (design review [7e]): the queue now, what every
    /// organisation's applications sent since a time, how much went on the first try, and the
    /// receiving servers that slow delivery down.
    /// </summary>
    /// <param name="since">From (the start of the operator's day).</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The figures.</returns>
    public async Task<TransactionalView> TransactionalAsync(DateTimeOffset since, DateTimeOffset now, CancellationToken ct = default)
    {
        long waiting = await this.messageStore.CountOutboundAsync(OutboundStatus.Pending, ct).ConfigureAwait(false)
            + await this.messageStore.CountOutboundAsync(OutboundStatus.Sending, ct).ConfigureAwait(false);
        DateTimeOffset? oldest = await this.messageStore.OldestPendingOutboundAsync(ct).ConfigureAwait(false);
        var senders = new List<string>();
        int orgs = 0;
        foreach (TenantRow t in await this.store.ListTenantsAsync(ct).ConfigureAwait(false))
        {
            IReadOnlyList<string> mine = await DoNotSend.AppSendersAsync(this.store, t.Id, ct).ConfigureAwait(false);
            if (mine.Count > 0)
            {
                orgs++;
                senders.AddRange(mine);
            }
        }
        IReadOnlyList<OutboundMessage> rows = senders.Count == 0
            ? Array.Empty<OutboundMessage>()
            : await this.messageStore.ListOutboundBySendersAsync(senders, since, now, SendingRowLimit, ct).ConfigureAwait(false);
        int sent = rows.Count(m => m.Status == OutboundStatus.Sent);
        int firstTry = rows.Count(m => m.Status == OutboundStatus.Sent && m.Attempts <= 1);
        IReadOnlyList<RecipientDomainTraffic> domains = await this.messageStore.CountOutboundByRecipientDomainAsync(since, now, ct).ConfigureAwait(false);
        var slow = domains.Where(d => d.Waiting > 0 || d.Failed > 0)
            .OrderByDescending(d => d.Waiting).ThenByDescending(d => d.Failed).Take(8)
            .Select(d => new SlowReceiver(d.Domain, d.Waiting, d.Failed,
                rows.Where(m => m.LastError.Length > 0 && string.Equals(DomainOf(m.EnvelopeTo), d.Domain, StringComparison.OrdinalIgnoreCase)).OrderByDescending(m => m.CreatedAt).Select(m => m.LastError).FirstOrDefault() ?? string.Empty))
            .ToList();
        return new TransactionalView(waiting, oldest is DateTimeOffset o ? now - o : null, rows.Count, orgs, sent, firstTry, slow);
    }

    /// <summary>The messages a Messages tab shows: by status, application and words.</summary>
    /// <param name="rows">Every message in the period.</param>
    /// <param name="status">A status, or null or "all".</param>
    /// <param name="appId">An application's id, or null or "all".</param>
    /// <param name="q">Words in the address, subject, message's id or the reply; "@domain" matches a domain.</param>
    /// <returns>The messages that match.</returns>
    public static IEnumerable<SendingRow> FilterSending(IEnumerable<SendingRow> rows, string? status, string? appId, string? q)
    {
        ArgumentNullException.ThrowIfNull(rows);
        string words = (q ?? string.Empty).Trim();
        return rows.Where(r => (string.IsNullOrEmpty(status) || status == "all" || string.Equals(r.Status, status, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrEmpty(appId) || appId == "all" || r.AppId == appId)
            && (words.Length == 0 || r.To.Contains(words, StringComparison.OrdinalIgnoreCase) || r.Subject.Contains(words, StringComparison.OrdinalIgnoreCase)
                || r.Id.ToString().StartsWith(words, StringComparison.OrdinalIgnoreCase) || r.LastError.Contains(words, StringComparison.OrdinalIgnoreCase)));
    }

    private static string DomainOf(string address) => address[(address.LastIndexOf('@') + 1)..].Trim().ToLowerInvariant();

    /// <summary>A header's value, unfolded and decoded, from a message's first bytes; empty when absent.</summary>
    /// <param name="raw">The message (its headers are enough).</param>
    /// <param name="name">The header's name.</param>
    /// <returns>The value.</returns>
    public static string HeaderOf(byte[] raw, string name)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(name);
        string text = Encoding.UTF8.GetString(raw, 0, Math.Min(raw.Length, 8192)).Replace("\r\n", "\n", StringComparison.Ordinal);
        int end = text.IndexOf("\n\n", StringComparison.Ordinal);
        string head = end >= 0 ? text[..end] : text;
        string[] lines = head.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            {
                var value = new StringBuilder(lines[i][(name.Length + 1)..].Trim());
                for (int j = i + 1; j < lines.Length && lines[j].Length > 0 && (lines[j][0] == ' ' || lines[j][0] == '\t'); j++)
                {
                    value.Append(' ').Append(lines[j].Trim());
                }
                return Anjal.Mime.EncodedWordDecoder.Decode(value.ToString());
            }
        }
        return string.Empty;
    }
}
