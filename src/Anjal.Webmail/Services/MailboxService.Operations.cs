using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>One organisation in the Anjal console (rc.14, board OpsOrgs).</summary>
/// <param name="Tenant">The organisation.</param>
/// <param name="People">How many people (shared mailboxes are not counted).</param>
/// <param name="Domains">Its domains.</param>
/// <param name="DkimMissing">Its domains without a DKIM key.</param>
/// <param name="UsedBytes">Storage in use, across its mailboxes.</param>
/// <param name="Ops">What the operator records for it.</param>
/// <param name="Admins">Its administrators' addresses.</param>
/// <param name="Plan">Its storage plan (DES-11 D2); "none" when not chosen.</param>
/// <param name="Mailboxes">How many mailboxes it has, shared ones included (each gets the plan's size).</param>
public sealed record OrgSummary(TenantRow Tenant, int People, IReadOnlyList<string> Domains, IReadOnlyList<string> DkimMissing, long UsedBytes, OpsRecord Ops, IReadOnlyList<string> Admins, StoragePlan? Plan = null, int Mailboxes = 0)
{
    /// <summary>
    /// The storage Anjal gives the organisation under its plan (DES-11 D2): each mailbox's size times
    /// its mailboxes, the shared total, or both for a mixed plan; Anjal's 1 GB each when no plan.
    /// </summary>
    public long GivenBytes => (this.Plan ?? new StoragePlan()).Plan switch
    {
        "person" => this.Plan!.PersonBytes * this.Mailboxes,
        "shared" => this.Plan!.SharedBytes,
        "mixed" => (this.Plan!.PersonBytes * this.Mailboxes) + this.Plan.SharedBytes,
        _ => MailboxRow.DefaultQuotaBytes * this.Mailboxes,
    };

    /// <summary>"active", "setting-up", "trial" or "suspended".</summary>
    public string Status => !this.Tenant.Enabled ? "suspended" : this.Ops.Status;
}

/// <summary>The server's disk and memory (rc.15, item 61).</summary>
/// <param name="DiskTotal">The disk holding mail, in bytes; 0 when not measured.</param>
/// <param name="DiskUsed">Of which used.</param>
/// <param name="Mail">Mail's share.</param>
/// <param name="Evidence">Evidence's share.</param>
/// <param name="MemoryUsed">This process's memory.</param>
/// <param name="MemoryTotal">The memory the process can use.</param>
/// <param name="Backups">rc.15 (item 61): the backup copies waiting on this server to be sent off (ANJAL_BACKUP_SCRATCH).</param>
public sealed record ServiceCapacity(long DiskTotal, long DiskUsed, long Mail, long Evidence, long MemoryUsed, long MemoryTotal, long Backups = 0, long Database = 0);

/// <summary>One tile of the service health (rc.14, board OpsHealth).</summary>
/// <param name="Title">What it is, for example "Delivery queue".</param>
/// <param name="Text">What was found.</param>
/// <param name="Level">"ok", "warn", "bad" or "unknown".</param>
/// <param name="Args">Values for the blanks in Text, such as {n}: the words are looked up with their blanks, then filled.</param>
public sealed record HealthTile(string Title, string Text, string Level, IReadOnlyDictionary<string, string>? Args = null)
{
    /// <summary>The text with its blanks filled, in English (for the operators' summary mail).</summary>
    public string Filled => Fill(this.Text, this.Args);

    /// <summary>Fill a text's blanks.</summary>
    /// <param name="text">The text, for example "{n} days left".</param>
    /// <param name="args">The values.</param>
    /// <returns>The text filled.</returns>
    public static string Fill(string text, IReadOnlyDictionary<string, string>? args)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (KeyValuePair<string, string> a in args ?? new Dictionary<string, string>())
        {
            text = text.Replace("{" + a.Key + "}", a.Value, StringComparison.Ordinal);
        }
        return text;
    }
}

/// <summary>
/// The Anjal console (rc.14, boards OpsOrgs, OpsLimits, OpsHealth): the
/// organisations on this service, their limits, and the service's health.
/// Operators manage the service, never anyone's mail.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>Every organisation, by name, with what the console shows.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The organisations.</returns>
    public async Task<IReadOnlyList<OrgSummary>> ListOrganisationsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<TenantDomainRow> allDomains = await this.store.ListTenantDomainsAsync(null, ct).ConfigureAwait(false);
        HashSet<string> keyed = (await this.messageStore.ListDkimKeysAsync(ct).ConfigureAwait(false)).Select(k => k.Domain.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var result = new List<OrgSummary>();
        foreach (TenantRow t in await this.store.ListTenantsAsync(ct).ConfigureAwait(false))
        {
            IReadOnlyList<MailboxRow> boxes = await this.store.ListMailboxesAsync(t.Id, ct).ConfigureAwait(false);
            Dictionary<Guid, SharedMailbox> shared = await this.SharedOfTenantAsync(t.Id, ct).ConfigureAwait(false);
            List<string> domains = allDomains.Where(d => d.TenantId == t.Id).Select(d => d.Domain).OrderBy(d => d, StringComparer.Ordinal).ToList();
            IReadOnlyList<Guid> admins = await this.AdminsOfAsync(t, ct).ConfigureAwait(false);
            OpsRecord ops = await this.ReadTenantDocumentAsync<OpsRecord>(t.Id, OpsKind, ct).ConfigureAwait(false) ?? new OpsRecord();
            result.Add(new OrgSummary(
                t,
                boxes.Count(b => !shared.ContainsKey(b.Id)),
                domains,
                domains.Where(d => !keyed.Contains(d)).ToList(),
                boxes.Sum(b => b.UsedBytes),
                ops,
                boxes.Where(b => admins.Contains(b.Id)).Select(b => b.Address).ToList(),
                await StoragePlan.ReadAsync(this.store, t.Id, ct).ConfigureAwait(false),
                boxes.Count));
        }
        return result.OrderBy(o => o.Tenant.DisplayName.Length > 0 ? o.Tenant.DisplayName : o.Tenant.Slug, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Add an organisation: its record, its first domain (the operator vouches
    /// for it), a DKIM key, and its first administrator, invited. Returns the
    /// error, or the organisation and the invitation's token.
    /// </summary>
    /// <param name="name">The organisation's name.</param>
    /// <param name="domain">Its first domain.</param>
    /// <param name="adminName">The first administrator's name.</param>
    /// <param name="adminAddress">Their new address, at the domain.</param>
    /// <param name="personal">Their personal address, to send the invitation to, or empty.</param>
    /// <param name="status">"active", "setting-up" or "trial".</param>
    /// <param name="invitedBy">The operator, by name.</param>
    /// <param name="baseUrl">The webmail's address, for the link.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or the organisation and token.</returns>
    public async Task<(string? Error, TenantRow? Tenant, string? Token)> CreateOrganisationAsync(string name, string domain, string adminName, string adminAddress, string personal, string status, string invitedBy, string baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(adminAddress);
        string n = Clip(name.Trim(), 100);
        string d = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (n.Length == 0)
        {
            return ("Give the organisation a name.", null, null);
        }
        if (!OrgEndpointsShared.IsDomainName(d))
        {
            return ("That is not a domain name.", null, null);
        }
        if (await this.store.GetTenantDomainAsync(d, ct).ConfigureAwait(false) is not null)
        {
            return ("That domain is already in use on this service.", null, null);
        }
        if (!adminAddress.Trim().EndsWith("@" + d, StringComparison.OrdinalIgnoreCase))
        {
            return ($"The administrator's address must be at {d}.", null, null);
        }
        string slug = await this.FreeSlugAsync(d, ct).ConfigureAwait(false);
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = slug, DisplayName = n, Enabled = true }, ct).ConfigureAwait(false);
        await this.WriteTenantDocumentAsync(tenant.Id, OpsKind, new OpsRecord { Status = status is "active" or "trial" ? status : "setting-up" }, ct).ConfigureAwait(false);
        string? error = await this.AddTenantDomainAsync(tenant, d, ct).ConfigureAwait(false);
        if (error is not null)
        {
            return (error, null, null);
        }
        (string? inviteError, string? token) = await this.InviteAsync(tenant, adminName ?? string.Empty, adminAddress, personal ?? string.Empty, true, invitedBy ?? string.Empty, baseUrl ?? string.Empty, ct).ConfigureAwait(false);
        return (inviteError, tenant, token);
    }

    /// <summary>Save the operator's record of an organisation: its status and limits. Returns the error, or null.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="record">The record.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> SaveOpsRecordAsync(Guid tenantId, OpsRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Status is not ("active" or "setting-up" or "trial"))
        {
            return "Choose one of the statuses listed.";
        }
        if (record.PeopleLimit is < 1 or > 100_000 || record.SendPerDay is < 1 or > 1_000_000)
        {
            return "Limits are whole numbers: 1 to 100,000 people and GB, 1 to 1,000,000 messages a day.";
        }
        await this.WriteTenantDocumentAsync(tenantId, OpsKind, record, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Suspend an organisation (no one can sign in or send; mail is kept and still received), or resume it.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="suspended">True to suspend.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The organisation, or null.</returns>
    public async Task<TenantRow?> SetSuspendedAsync(Guid tenantId, bool suspended, CancellationToken ct = default)
    {
        TenantRow? t = await this.store.GetTenantByIdAsync(tenantId, ct).ConfigureAwait(false);
        if (t is null)
        {
            return null;
        }
        t.Enabled = !suspended;
        return await this.store.UpsertTenantAsync(t, ct).ConfigureAwait(false);
    }

    /// <summary>The service's health, as the console's tiles (rc.14, board OpsHealth).</summary>
    /// <param name="maildirRoot">Where mail is kept, for the disk.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The tiles, and the arrivals per hour for the last 24 hours (oldest first).</returns>
    public async Task<(IReadOnlyList<HealthTile> Tiles, IReadOnlyList<(DateTimeOffset Hour, long Count)> Hours)> HealthAsync(string maildirRoot, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(maildirRoot);
        var tiles = new List<HealthTile>();

        DateTimeOffset? oldest = await this.messageStore.OldestPendingOutboundAsync(ct).ConfigureAwait(false);
        tiles.Add(oldest is DateTimeOffset o
            ? new HealthTile("Delivery queue", SpanWords(now - o, "Oldest waiting under a minute", "Oldest waiting {n} min", "Oldest waiting {n} h", "Oldest waiting {n} days"), now - o > TimeSpan.FromHours(6) ? "bad" : now - o > TimeSpan.FromMinutes(30) ? "warn" : "ok", SpanArgs(now - o))
            : new HealthTile("Delivery queue", "Nothing waiting", "ok"));

        tiles.Add(CertificateTile(now));
        tiles.Add(DiskTile(maildirRoot));
        tiles.Add(BackupTile(now));
        tiles.Add(await this.SealTileAsync(ct).ConfigureAwait(false));
        tiles.Add(LeakedPasswordsTile(now));
        tiles.Add(await this.ReputationTileAsync(ct).ConfigureAwait(false));
        string mtaSts = MtaStsPolicy.ConfiguredMode();
        tiles.Add(new HealthTile("TLS reports", mtaSts == "enforce" ? "MTA-STS enforced" : mtaSts == "testing" ? "MTA-STS in testing; reports to tls-reports@" : "MTA-STS off", mtaSts == "enforce" ? "ok" : "warn"));

        IReadOnlyList<OrgSummary> orgs = await this.ListOrganisationsAsync(ct).ConfigureAwait(false);
        int missing = orgs.Sum(x => x.DkimMissing.Count);
        tiles.Add(new HealthTile("DKIM", missing == 0 ? "Every domain has a key" : missing == 1 ? "1 domain without a key" : "{n} domains without a key", missing == 0 ? "ok" : "warn", N(missing)));
        foreach (OrgSummary org in orgs.Where(x => x.DkimMissing.Count > 0 && x.Tenant.Enabled).Take(3))
        {
            tiles.Add(new HealthTile("DKIM", "{org}: no DKIM key for {domains}", "warn", new Dictionary<string, string>(StringComparer.Ordinal) { ["org"] = org.Tenant.DisplayName.Length > 0 ? org.Tenant.DisplayName : org.Tenant.Slug, ["domains"] = string.Join(", ", org.DkimMissing) }));
        }

        DateTimeOffset start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset).AddHours(-23);
        Dictionary<DateTimeOffset, long> counted = (await this.store.CountArrivalsByHourAsync(start, ct).ConfigureAwait(false))
            .GroupBy(h => h.Hour.ToUniversalTime())
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Count));
        var hours = new List<(DateTimeOffset, long)>();
        for (int i = 0; i < 24; i++)
        {
            DateTimeOffset h = start.AddHours(i).ToUniversalTime();
            hours.Add((h, counted.TryGetValue(h, out long c) ? c : 0));
        }
        return (tiles, hours);
    }

    private static CapacityEntry? capacityCache;

    /// <summary>
    /// The server's disk and memory (rc.15, item 61, "server capacity"): the
    /// disk holding mail, how much of it is mail and how much evidence, and
    /// this process's memory. Folder sizes are counted at most every ten
    /// minutes; a large store takes a while to walk.
    /// </summary>
    /// <param name="maildirRoot">Where mail is kept.</param>
    /// <param name="evidenceRoot">Where evidence is kept.</param>
    /// <param name="now">Now.</param>
    /// <returns>The capacity.</returns>
    public static ServiceCapacity Capacity(string maildirRoot, string evidenceRoot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(maildirRoot);
        ArgumentNullException.ThrowIfNull(evidenceRoot);
        if (capacityCache is CapacityEntry c && now - c.At < TimeSpan.FromMinutes(10))
        {
            return c.Capacity;
        }
        long total = 0;
        long free = 0;
        try
        {
            var drive = new DriveInfo(Directory.Exists(maildirRoot) ? maildirRoot : Path.GetPathRoot(Path.GetFullPath(maildirRoot)) ?? "/");
            total = drive.TotalSize;
            free = drive.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unknown: shown as not measured.
        }
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        string backups = Environment.GetEnvironmentVariable("ANJAL_BACKUP_SCRATCH") is { Length: > 0 } b ? b : "/var/lib/anjal/backup";
        var capacity = new ServiceCapacity(total, total - free, FolderSize(maildirRoot), FolderSize(evidenceRoot), Environment.WorkingSet, memory.TotalAvailableMemoryBytes, FolderSize(backups));
        capacityCache = new CapacityEntry(now, capacity);
        return capacity;
    }

    private sealed record CapacityEntry(DateTimeOffset At, ServiceCapacity Capacity);

    private static long FolderSize(string root)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }
        long size = 0;
        try
        {
            foreach (FileInfo f in new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                size += f.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Partly counted: what was counted is shown.
        }
        return size;
    }

    /// <summary>The weekly summary's text (board OpsHealth, "Send it now").</summary>
    /// <param name="tiles">The health tiles.</param>
    /// <param name="hours">Arrivals per hour.</param>
    /// <param name="orgs">The organisations.</param>
    /// <returns>The text.</returns>
    public static string SummaryText(IReadOnlyList<HealthTile> tiles, IReadOnlyList<(DateTimeOffset Hour, long Count)> hours, IReadOnlyList<OrgSummary> orgs)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(hours);
        ArgumentNullException.ThrowIfNull(orgs);
        var sb = new System.Text.StringBuilder();
        sb.Append("Anjal service summary\r\n\r\n");
        foreach (HealthTile t in tiles)
        {
            string mark = t.Level switch { "ok" => "OK  ", "warn" => "LOOK", "bad" => "ACT ", _ => "N/A " };
            sb.Append(CultureInfo.InvariantCulture, $"{mark}  {t.Title}: {t.Filled}\r\n");
        }
        sb.Append(CultureInfo.InvariantCulture, $"\r\nMessages received in the last 24 hours: {hours.Sum(h => h.Count):N0}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"Organisations: {orgs.Count} ({orgs.Count(o => o.Status == "active")} active, {orgs.Count(o => o.Status == "suspended")} suspended)\r\n");
        sb.Append("\r\nOperators manage the service, never anyone's mail.\r\n");
        return sb.ToString();
    }

    // Of four whole sentences about a time span, the one that fits it; SpanArgs fills its {n}.
    private static string SpanWords(TimeSpan t, string underMinute, string minutes, string hours, string days) =>
        t < TimeSpan.FromMinutes(1) ? underMinute : t < TimeSpan.FromHours(1) ? minutes : t < TimeSpan.FromDays(2) ? hours : days;

    private static Dictionary<string, string> SpanArgs(TimeSpan t) =>
        N(t < TimeSpan.FromHours(1) ? (long)t.TotalMinutes : t < TimeSpan.FromDays(2) ? (long)t.TotalHours : (long)t.TotalDays);

    private static Dictionary<string, string> N(long n) => new Dictionary<string, string>(StringComparer.Ordinal) { ["n"] = n.ToString(CultureInfo.InvariantCulture) };

    private static HealthTile CertificateTile(DateTimeOffset now)
    {
        string? path = Environment.GetEnvironmentVariable("ANJAL_ACME_DOMAINS") is { Length: > 0 }
            ? new Anjal.Acme.CertificateStore(Anjal.Acme.AcmeEnvironment.Read(hostByDefault: true).Directory).FullChainPath
            : Environment.GetEnvironmentVariable("ANJAL_TLS_CERT_PATH");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return new HealthTile("Certificates", "No certificate set here", "unknown");
        }
        try
        {
            string pem = File.ReadAllText(path);
            System.Security.Cryptography.PemFields f = System.Security.Cryptography.PemEncoding.Find(pem);
            using X509Certificate2 cert = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(pem[f.Base64Data]));
            int days = (int)Math.Floor((cert.NotAfter.ToUniversalTime() - now.UtcDateTime).TotalDays);
            return new HealthTile("Certificates", days < 0 ? "Expired" : days == 1 ? "1 day left" : "{n} days left", days < 7 ? "bad" : days < 15 ? "warn" : "ok", N(days));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            return new HealthTile("Certificates", "The certificate could not be read", "bad");
        }
    }

    private static HealthTile DiskTile(string root)
    {
        try
        {
            string path = Directory.Exists(root) ? root : Path.GetPathRoot(Path.GetFullPath(root)) ?? "/";
            var drive = new DriveInfo(path);
            double total = drive.TotalSize;
            double used = total - drive.AvailableFreeSpace;
            int pct = Sizes.Percent((long)used, (long)total);
            return new HealthTile("Disk", "{p}% used ({used} of {total})", pct >= 90 ? "bad" : pct >= 80 ? "warn" : "ok", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["p"] = pct.ToString(CultureInfo.InvariantCulture),
                ["used"] = Sizes.Text((long)used),
                ["total"] = Sizes.Text((long)total),
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new HealthTile("Disk", "Could not be read", "unknown");
        }
    }

    /// <summary>
    /// The leaked-password list (rc.15, item 35): how many passwords it holds and when it was
    /// last refreshed (every six months, by the server), or how far a download has got. Every
    /// new password is also checked online unless the service is set otherwise.
    /// </summary>
    /// <param name="now">Now.</param>
    /// <returns>The tile.</returns>
    public static HealthTile LeakedPasswordsTile(DateTimeOffset now)
    {
        if (!Anjal.Smtp.PwnedPasswords.UsesDownload)
        {
            return Anjal.Smtp.PwnedPasswords.ChecksOnline
                ? new HealthTile("Leaked passwords", "Checked online only; no list kept here", "ok")
                : new HealthTile("Leaked passwords", "Not checked (switched off)", "warn");
        }
        Anjal.Smtp.PwnedPasswordList? list = Anjal.Smtp.PwnedPasswords.Current;
        Anjal.Smtp.PwnedRefreshStatus? status = Anjal.Smtp.PwnedRefreshStatus.Read(Anjal.Smtp.PwnedPasswords.ConfiguredPath);
        if (status is { State: "building" })
        {
            return new HealthTile("Leaked passwords", "Downloading: {n}% done", list is null ? "warn" : "ok", N(status.Percent));
        }
        if (list is null)
        {
            return status is { State: "failed" }
                ? new HealthTile("Leaked passwords", "Download failed; the server will try again in six hours", "bad")
                : new HealthTile("Leaked passwords", "Not downloaded yet; the short built-in list is used", "warn");
        }
        bool billions = list.Count >= 1_000_000_000;
        var args = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["n"] = billions
                ? (list.Count / 1_000_000_000.0).ToString("0.0", CultureInfo.InvariantCulture)
                : Math.Max(1, (int)Math.Round(list.Count / 1_000_000.0)).ToString("N0", CultureInfo.InvariantCulture),
            ["date"] = list.BuiltAt.ToString("d MMM yyyy", CultureInfo.InvariantCulture),
        };
        bool overdue = now - list.BuiltAt > TimeSpan.FromDays(200);
        string text = (billions, overdue) switch
        {
            (true, true) => "{n} billion, refreshed {date}; refresh overdue",
            (true, false) => "{n} billion, refreshed {date}",
            (false, true) => "{n} million, refreshed {date}; refresh overdue",
            _ => "{n} million, refreshed {date}",
        };
        return new HealthTile("Leaked passwords", text, overdue ? "warn" : "ok", args);
    }

    // When the last verified backup finished, from the record backup.sh writes; null when none or unreadable.
    private static DateTimeOffset? LastGoodBackup()
    {
        string file = Environment.GetEnvironmentVariable("ANJAL_BACKUP_STATUS") ?? "/var/lib/anjal/backup-status";
        try
        {
            return File.Exists(file) ? ReadBackupStatus(File.ReadAllText(file)).Good : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// What backup.sh's record says (D-88, owner 10 Oct 2026): a line "ok &lt;time&gt;" for the last
    /// verified backup, and a line "failed &lt;time&gt; ..." when a run has failed since, after its
    /// 30 minutes of tries. A record from before D-88 is one "ok" line.
    /// </summary>
    /// <param name="text">The file's contents.</param>
    /// <returns>The last good backup and the last failure, each null when not recorded.</returns>
    public static (DateTimeOffset? Good, DateTimeOffset? Failed) ReadBackupStatus(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        DateTimeOffset? good = null, failed = null;
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split(' ', 3);
            if (parts.Length >= 2 && DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at))
            {
                if (parts[0] == "ok")
                {
                    good = at;
                }
                else if (parts[0] == "failed")
                {
                    failed = at;
                }
            }
        }
        return (good, failed is DateTimeOffset f && (good is null || f > good) ? failed : null);
    }

    /// <summary>
    /// DES-11 S6: the mail server's seal key, which locks every DKIM key - made, and in a verified
    /// backup since. Losing it unbacked would mean new DKIM keys and new DNS records everywhere.
    /// </summary>
    private async Task<HealthTile> SealTileAsync(CancellationToken ct)
    {
        SealRecord? seal = await KeySeal.ReadRecordAsync(this.messageStore, ct).ConfigureAwait(false);
        if (seal is null)
        {
            return new HealthTile("DKIM key lock", "Not made yet: start the mail server once", "warn");
        }
        DateTimeOffset? backup = LastGoodBackup();
        var args = new Dictionary<string, string>(StringComparer.Ordinal) { ["date"] = ZonedClock.Default.Date(seal.Made) };
        return backup is DateTimeOffset at && at >= seal.Made
            ? new HealthTile("DKIM key lock", "Made {date}; in the backup of {backup}", "ok", new Dictionary<string, string>(args, StringComparer.Ordinal) { ["backup"] = ZonedClock.Default.Date(at) })
            : new HealthTile("DKIM key lock", "Made {date}; not in a backup yet", "warn", args);
    }

    private static HealthTile BackupTile(DateTimeOffset now)
    {
        string file = Environment.GetEnvironmentVariable("ANJAL_BACKUP_STATUS") ?? "/var/lib/anjal/backup-status";
        try
        {
            return BackupTile(File.Exists(file) ? File.ReadAllText(file) : string.Empty, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HealthTile("Last good backup", "The record could not be read", "unknown");
        }
    }

    /// <summary>
    /// The "Last good backup" tile from backup.sh's record. A run that failed after its 30 minutes
    /// of tries turns it red at once, so the operators are mailed within 15 minutes (D-88, owner 10
    /// Oct 2026), not when the last good backup is 48 hours old.
    /// </summary>
    /// <param name="record">The record's contents; empty when there is none.</param>
    /// <param name="now">Now.</param>
    /// <returns>The tile.</returns>
    public static HealthTile BackupTile(string record, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(record);
        (DateTimeOffset? good, DateTimeOffset? failed) = ReadBackupStatus(record);
        if (failed is DateTimeOffset f)
        {
            var args = new Dictionary<string, string>(StringComparer.Ordinal) { ["date"] = ZonedClock.Default.Full(f) };
            return new HealthTile("Last good backup", "Failed {date} after 30 minutes of tries; see the backup log", "bad", args);
        }
        if (good is DateTimeOffset at)
        {
            TimeSpan age = now - at;
            return new HealthTile("Last good backup", SpanWords(age, "Under a minute ago, verified", "{n} min ago, verified", "{n} h ago, verified", "{n} days ago, verified"), age > TimeSpan.FromHours(48) ? "bad" : age > TimeSpan.FromHours(26) ? "warn" : "ok", SpanArgs(age));
        }
        return new HealthTile("Last good backup", "No verified backup recorded", "bad");
    }

    // The service's address against two blocklists. A list that answers
    // 127.255.255.x refuses the resolver used; that is "not checked", never "listed".
    private async Task<HealthTile> ReputationTileAsync(CancellationToken ct)
    {
        IPAddress? ip;
        try
        {
            using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
            t.CancelAfter(TimeSpan.FromSeconds(3));
            ip = (await System.Net.Dns.GetHostAddressesAsync(this.hostName, AddressFamily.InterNetwork, t.Token).ConfigureAwait(false)).FirstOrDefault();
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            ip = null;
        }
        if (ip is null || IPAddress.IsLoopback(ip))
        {
            return new HealthTile("Reputation", "Not checked: the service's address is not public", "unknown");
        }
        string reversed = string.Join('.', ip.GetAddressBytes().Reverse());
        var listed = new List<string>();
        int checkedLists = 0;
        foreach (string list in new[] { "zen.spamhaus.org", "bl.spamcop.net" })
        {
            try
            {
                using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
                t.CancelAfter(TimeSpan.FromSeconds(3));
                IPAddress[] answer = await System.Net.Dns.GetHostAddressesAsync(reversed + "." + list, AddressFamily.InterNetwork, t.Token).ConfigureAwait(false);
                if (answer.Any(a => a.GetAddressBytes() is [127, 255, 255, _]))
                {
                    continue;
                }
                checkedLists++;
                if (answer.Length > 0)
                {
                    listed.Add(list);
                }
            }
            catch (SocketException)
            {
                checkedLists++;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Not answered in time: not checked.
            }
        }
        return listed.Count > 0 ? new HealthTile("Reputation", "Listed on {lists}", "bad", new Dictionary<string, string>(StringComparer.Ordinal) { ["lists"] = string.Join(", ", listed) })
            : checkedLists == 0 ? new HealthTile("Reputation", "Blocklists did not answer", "unknown")
            : new HealthTile("Reputation", checkedLists == 1 ? "Not on the 1 blocklist checked" : "Not on the {n} blocklists checked", "ok", N(checkedLists));
    }

    private async Task<string> FreeSlugAsync(string domain, CancellationToken ct)
    {
        string root = new string(domain.Split('.')[0].Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray()).ToLowerInvariant();
        root = root.Length == 0 ? "org" : root;
        string slug = root;
        for (int i = 2; await this.store.GetTenantAsync(slug, ct).ConfigureAwait(false) is not null; i++)
        {
            slug = root + "-" + i.ToString(CultureInfo.InvariantCulture);
        }
        return slug;
    }
}

/// <summary>Checks shared by the two consoles.</summary>
internal static class OrgEndpointsShared
{
    /// <summary>A name that can be a domain: letters, digits and hyphens in dotted labels.</summary>
    /// <param name="d">The candidate.</param>
    /// <returns>Whether it can be.</returns>
    internal static bool IsDomainName(string d) =>
        d.Length is > 3 and < 254 && d.Contains('.', StringComparison.Ordinal)
        && d.Split('.').All(l => l.Length is > 0 and < 64 && l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && l[0] != '-' && l[^1] != '-');
}
