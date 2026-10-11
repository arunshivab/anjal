using System.Net;
using Anjal.Mime;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>A network a person has signed in from (rc.15, item 59).</summary>
public sealed class SeenNetwork
{
    /// <summary>The network, for example "203.0.113.0/24".</summary>
    public string Net { get; set; } = string.Empty;

    /// <summary>Where it is, for people to read; empty when not known.</summary>
    public string Place { get; set; } = string.Empty;

    /// <summary>The first sign-in from it.</summary>
    public DateTimeOffset First { get; set; }

    /// <summary>The latest sign-in from it.</summary>
    public DateTimeOffset Last { get; set; }
}

/// <summary>A sign-in from a network the person had not used before (rc.15, item 59).</summary>
/// <param name="Who">The address that signed in.</param>
/// <param name="Place">Where, for example "Pune, India"; empty when not known.</param>
/// <param name="At">When.</param>
public sealed record NewNetworkSignIn(string Who, string Place, DateTimeOffset At);

/// <summary>The daily check of an organisation's DNS records (rc.15, item 59).</summary>
public sealed class DnsCheckRecord
{
    /// <summary>When it was made.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>For each domain, each record's kind (MX, SPF, DKIM, DMARC, MTA-STS, TLS reports) and what was found.</summary>
    public Dictionary<string, Dictionary<string, string>> Domains { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// rc.15 (item 59): what the organisation's dashboard shows beyond the
/// totals - sign-ins from new networks with town and country, the daily
/// check of each domain's DNS records, the TLS reports receivers send, and
/// what was refused for its applications.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The kind of the document listing the networks a person has signed in from.</summary>
    public const string NetworksKind = "sign-in-networks";

    /// <summary>The kind of the organisation document holding the latest DNS check.</summary>
    public const string DnsCheckKind = "dns-check";

    /// <summary>The kind of the organisation document holding TLS reports already read, by message.</summary>
    public const string TlsReportsKind = "tls-reports";

    /// <summary>The record kinds the daily check reports, in the order shown.</summary>
    public static readonly IReadOnlyList<string> CheckedRecords = new[] { "MX", "SPF", "DKIM", "DMARC", "MTA-STS" };

    /// <summary>
    /// Note the network a sign-in came from. It is new when the person has
    /// signed in before and never from this network; a first sign-in, and one
    /// from this computer or a private network, is never new.
    /// </summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="remoteAddress">The address the sign-in came from.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether the network is new, and where it is.</returns>
    public async Task<(bool IsNew, string Place)> NoteSignInNetworkAsync(Guid mailboxId, string remoteAddress, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(remoteAddress ?? string.Empty, out IPAddress? ip))
        {
            return (false, string.Empty);
        }
        if (IpLocations.IsPrivate(ip))
        {
            return (false, string.Empty);
        }
        string net = IpLocations.NetworkOf(ip);
        List<SeenNetwork> seen = await this.ReadDocumentAsync<List<SeenNetwork>>(mailboxId, NetworksKind, ct).ConfigureAwait(false) ?? new List<SeenNetwork>();
        SeenNetwork? known = seen.FirstOrDefault(n => n.Net == net);
        string place = known?.Place is { Length: > 0 } p ? p : IpLocations.Find(ip)?.Text ?? string.Empty;
        bool isNew = known is null && seen.Count > 0;
        if (known is null)
        {
            seen.Add(new SeenNetwork { Net = net, Place = place, First = now, Last = now });
        }
        else
        {
            known.Last = now;
            known.Place = place;
        }
        // The 50 used most recently are kept.
        await this.WriteDocumentAsync(mailboxId, NetworksKind, seen.OrderByDescending(n => n.Last).Take(50).ToList(), ct).ConfigureAwait(false);
        return (isNew, place);
    }

    /// <summary>Sign-ins from new networks since a moment, newest first, for some addresses (from the activity log).</summary>
    /// <param name="addresses">The addresses.</param>
    /// <param name="since">From when.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The sign-ins.</returns>
    public async Task<IReadOnlyList<NewNetworkSignIn>> NewNetworkSignInsAsync(IReadOnlyCollection<string> addresses, DateTimeOffset since, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        var set = new HashSet<string>(addresses, StringComparer.OrdinalIgnoreCase);
        var found = new List<NewNetworkSignIn>();
        await this.ScanAuditAsync(since, e =>
        {
            if (e.Action == "webmail.signin.new-network" && set.Contains(e.Subject))
            {
                found.Add(new NewNetworkSignIn(e.Subject, e.Detail, e.At));
            }
        }, ct).ConfigureAwait(false);
        return found;
    }

    /// <summary>Submissions refused for some user names since a moment (rc.15, item 59: an application's keys).</summary>
    /// <param name="usernames">The keys' user names.</param>
    /// <param name="since">From when.</param>
    /// <param name="until">Until when (not included).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many, by user name.</returns>
    public async Task<IReadOnlyDictionary<string, int>> RefusedSubmissionsAsync(IReadOnlyCollection<string> usernames, DateTimeOffset since, DateTimeOffset until, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usernames);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var set = new HashSet<string>(usernames, StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0)
        {
            return counts;
        }
        await this.ScanAuditAsync(since, e =>
        {
            if (e.Action == "smtp.submission.refused" && e.At < until && set.Contains(e.Subject))
            {
                counts[e.Subject] = (counts.TryGetValue(e.Subject, out int n) ? n : 0) + 1;
            }
        }, ct).ConfigureAwait(false);
        return counts;
    }

    /// <summary>The latest daily check of an organisation's DNS records, or null before the first.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The check.</returns>
    public Task<DnsCheckRecord?> DnsCheckOfAsync(Guid tenantId, CancellationToken ct = default) =>
        this.ReadTenantDocumentAsync<DnsCheckRecord>(tenantId, DnsCheckKind, ct);

    /// <summary>
    /// Check every organisation's domains whose last check is a day old (or
    /// never made): MX, SPF, DKIM, DMARC, MTA-STS and TLS reports, as the
    /// Domains page judges them. A record that could not be asked keeps its
    /// last answer.
    /// </summary>
    /// <param name="now">Now.</param>
    /// <param name="lookup">Asks the DNS: TXT at a name, or MX for "MX:name"; null when it could not be asked.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many organisations were checked.</returns>
    public async Task<int> CheckDnsDailyAsync(DateTimeOffset now, Func<string, Task<IReadOnlyList<string>?>> lookup, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        int checkedOrgs = 0;
        foreach (TenantRow t in await this.store.ListTenantsAsync(ct).ConfigureAwait(false))
        {
            if (!t.Enabled)
            {
                continue;
            }
            DnsCheckRecord? last = await this.DnsCheckOfAsync(t.Id, ct).ConfigureAwait(false);
            if (last is not null && now - last.At < TimeSpan.FromHours(23))
            {
                continue;
            }
            var record = new DnsCheckRecord { At = now };
            foreach (TenantDomainRow d in await this.store.ListTenantDomainsAsync(t.Id, ct).ConfigureAwait(false))
            {
                var found = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (DnsRecordCheck r in await this.DomainRecordsAsync(d.Domain, lookup, ct).ConfigureAwait(false))
                {
                    found[r.Kind] = r.Status == "unchecked" && last?.Domains.TryGetValue(d.Domain, out Dictionary<string, string>? before) == true && before.TryGetValue(r.Kind, out string? was) ? was : r.Status;
                }
                record.Domains[d.Domain] = found;
            }
            await this.WriteTenantDocumentAsync(t.Id, DnsCheckKind, record, ct).ConfigureAwait(false);
            checkedOrgs++;
        }
        return checkedOrgs;
    }

    /// <summary>
    /// What receivers reported about delivering to an organisation's domains
    /// (RFC 8460 TLS reports received in its mailboxes in a period): each
    /// reporter's encrypted and failed sessions. Each report is read once and kept.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="period">The period.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rows, by reporter, and how many reports they come from.</returns>
    public async Task<(IReadOnlyList<TlsReportRow> Rows, int Reports)> TlsReportsAsync(TenantRow tenant, DashPeriod period, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(period);
        Dictionary<Guid, List<TlsReportRow>> read = await this.ReadTenantDocumentAsync<Dictionary<Guid, List<TlsReportRow>>>(tenant.Id, TlsReportsKind, ct).ConfigureAwait(false) ?? new Dictionary<Guid, List<TlsReportRow>>();
        bool changed = false;
        HashSet<string> ours = (await this.store.ListTenantDomainsAsync(tenant.Id, ct).ConfigureAwait(false)).Select(d => d.Domain.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var rows = new List<TlsReportRow>();
        int reports = 0;
        foreach (MailboxRow box in await this.store.ListMailboxesAsync(tenant.Id, ct).ConfigureAwait(false))
        {
            var messages = new List<MessageRow>();
            foreach (string name in new[] { DmarcFolder, FolderRow.Inbox, Anjal.Mailbox.MailboxSink.JunkFolder })
            {
                if (await this.GetFolderAsync(box.Id, name, ct).ConfigureAwait(false) is FolderRow folder)
                {
                    messages.AddRange((await this.store.ListMessagesAsync(box.Id, folder.Id, 500, 0, ct).ConfigureAwait(false))
                        .Where(m => m.HasAttachments && EncodedWordDecoder.Decode(m.Subject).TrimStart().StartsWith("Report Domain:", StringComparison.OrdinalIgnoreCase)));
                }
            }
            foreach (MessageRow m in messages.Where(m => m.ReceivedAt >= period.Start.AddDays(-2) && m.ReceivedAt < period.End.AddDays(2)))
            {
                if (!read.TryGetValue(m.Id, out List<TlsReportRow>? found))
                {
                    found = new List<TlsReportRow>();
                    foreach (AttachmentView a in await this.ListAttachmentsAsync(box.Id, m.Id, ct).ConfigureAwait(false) ?? Array.Empty<AttachmentView>())
                    {
                        if (TlsReports.MayBeReport(a.FileName, a.ContentType) && await this.GetAttachmentAsync(box.Id, m.Id, a.Index, ct).ConfigureAwait(false) is { } got
                            && TlsReports.Read(got.Bytes) is IReadOnlyList<TlsReportRow> report)
                        {
                            found.AddRange(report);
                        }
                    }
                    read[m.Id] = found;
                    changed = true;
                }
                List<TlsReportRow> mine = found.Where(r => ours.Contains(r.Domain)).ToList();
                if (mine.Count > 0)
                {
                    reports++;
                    rows.AddRange(mine);
                }
            }
        }
        if (changed)
        {
            await this.WriteTenantDocumentAsync(tenant.Id, TlsReportsKind, read.TakeLast(1000).ToDictionary(kv => kv.Key, kv => kv.Value), ct).ConfigureAwait(false);
        }
        IReadOnlyList<TlsReportRow> byReporter = rows
            .GroupBy(r => r.Reporter.Length > 0 ? r.Reporter : "?", StringComparer.OrdinalIgnoreCase)
            .Select(g => new TlsReportRow(g.Key, string.Join(", ", g.Select(r => r.Domain).Distinct()), string.Join(", ", g.Select(r => r.Policy).Distinct()), g.Sum(r => r.Successful), g.Sum(r => r.Failed),
                string.Join(", ", g.SelectMany(r => r.Failures.Split(", ", StringSplitOptions.RemoveEmptyEntries)).Distinct().Take(5))))
            .OrderByDescending(r => r.Successful + r.Failed)
            .ToList();
        return (byReporter, reports);
    }

    // Each entry of the activity log since a moment, newest first.
    private async Task ScanAuditAsync(DateTimeOffset since, Action<AuditEvent> each, CancellationToken ct)
    {
        DateTimeOffset? before = null;
        for (int page = 0; page < 20; page++)
        {
            IReadOnlyList<AuditEvent> batch = await this.messageStore.ListAuditAsync(1000, before, ct).ConfigureAwait(false);
            foreach (AuditEvent e in batch)
            {
                if (e.At < since)
                {
                    return;
                }
                each(e);
            }
            if (batch.Count < 1000)
            {
                return;
            }
            before = batch[^1].At;
        }
    }
}
