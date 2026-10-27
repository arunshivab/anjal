using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Anjal.Mailbox;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>
/// One production (rc.26, D-266): an administrator's download of people's mail for a legal hold
/// (eDiscovery) or a leaver's records, made through the organisation's recovery key.
/// </summary>
public sealed class Production
{
    /// <summary>Its id.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>"hold" (eDiscovery for a legal hold) or "leaver" (a person's records when they leave).</summary>
    public string Kind { get; set; } = "hold";

    /// <summary>The administrator who asked; only they can download it.</summary>
    public Guid By { get; set; }

    /// <summary>The people whose mail is in it.</summary>
    public List<Guid> People { get; set; } = new();

    /// <summary>From (inclusive); null for all time.</summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>To (exclusive); null for now.</summary>
    public DateTimeOffset? To { get; set; }

    /// <summary>Why, as the administrator wrote it (the hold's reason, or the leaver's name).</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>When it was asked for.</summary>
    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>When it was ready; null while being made.</summary>
    public DateTimeOffset? ReadyAt { get; set; }

    /// <summary>The file on the server (sealed with the administrator's data key).</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>Its size.</summary>
    public long Bytes { get; set; }

    /// <summary>Mails in it.</summary>
    public int Mails { get; set; }

    /// <summary>What went wrong, or empty.</summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>True while it is being made.</summary>
    public bool Busy => this.ReadyAt is null && this.Error.Length == 0;

    /// <summary>True when it can be downloaded now (within seven days of being made).</summary>
    /// <param name="now">Now.</param>
    /// <returns>Whether.</returns>
    public bool Ready(DateTimeOffset now) => this.ReadyAt is DateTimeOffset at && at + ExportState.Life > now && this.File.Length > 0;
}

/// <summary>
/// rc.26 (D-266, DES-16 §2.7 and §2.8): eDiscovery production and a leaver's records. An administrator
/// whose session holds the organisation's recovery key chooses people and a period; their mail is opened
/// through the recovery key and written as .eml files with a CSV index and each mail's evidence record,
/// into one zip sealed on disk with the administrator's own data key, kept seven days and opened only as
/// that administrator downloads it. Every production is in the activity log and mailed to every administrator.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The organisation document listing productions.</summary>
    public const string ProductionsKind = "productions";

    /// <summary>The organisation's productions, newest first.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The list.</returns>
    public async Task<IReadOnlyList<Production>> ProductionsAsync(Guid tenantId, CancellationToken ct = default) =>
        (await this.ReadTenantDocumentAsync<List<Production>>(tenantId, ProductionsKind, ct).ConfigureAwait(false) ?? new List<Production>())
            .OrderByDescending(p => p.RequestedAt).ToList();

    /// <summary>
    /// Start a production. Returns the error, or null; the file is made in the background and the
    /// administrator is mailed when it is ready.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="adminId">The administrator (their session must hold the recovery key and their own keys).</param>
    /// <param name="kind">"hold" or "leaver".</param>
    /// <param name="people">The people.</param>
    /// <param name="from">From, or null.</param>
    /// <param name="to">To, or null.</param>
    /// <param name="reason">Why.</param>
    /// <param name="maildirRoot">Where mail files live; the file goes under its exports folder.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> ProduceAsync(TenantRow tenant, Guid adminId, string kind, IReadOnlyList<Guid> people, DateTimeOffset? from, DateTimeOffset? to, string reason, string maildirRoot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(maildirRoot);
        if (people.Count == 0)
        {
            return "Choose at least one person.";
        }
        if (this.KeyManager is not MailKeyManager km)
        {
            return "Encryption is not set up on this server.";
        }
        byte[]? adminData = MailKeyring.Current.DataKey(adminId);
        if (adminData is null)
        {
            return "Your own mail is not open in this session. Sign in with your password and try again.";
        }
        byte[]? recovery = await this.RecoveryKeyHereAsync(tenant, adminId, ct).ConfigureAwait(false);
        if (recovery is null)
        {
            return "Your session does not hold the organisation's recovery key. Sign in with your password and try again.";
        }
        var opened = new List<(MailboxRow Mailbox, UnlockedKeys Keys)>();
        try
        {
            foreach (Guid id in people.Distinct())
            {
                MailboxRow? m = await this.store.GetMailboxByIdAsync(id, ct).ConfigureAwait(false);
                if (m is null || m.TenantId != tenant.Id)
                {
                    return "That person is not in this organisation.";
                }
                UnlockedKeys? keys = await km.OpenMailboxByOrgAsync(id, recovery, ct).ConfigureAwait(false);
                if (keys is null && await km.Store.GetMailboxKeysAsync(id, ct).ConfigureAwait(false) is not null)
                {
                    return "The mail of " + m.Address + " could not be opened with the organisation's recovery key.";
                }
                if (keys is not null)
                {
                    opened.Add((m, keys));
                }
                else
                {
                    // Not encrypted yet (never signed in since encryption began): read as it is.
                    opened.Add((m, new UnlockedKeys(Array.Empty<byte>(), Array.Empty<byte>())));
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recovery);
        }
        var production = new Production
        {
            Kind = kind == "leaver" ? "leaver" : "hold",
            By = adminId,
            People = opened.Select(o => o.Mailbox.Id).ToList(),
            From = from,
            To = to,
            Reason = Clip(reason.Trim(), 200),
            RequestedAt = DateTimeOffset.UtcNow,
        };
        List<Production> all = (await this.ReadTenantDocumentAsync<List<Production>>(tenant.Id, ProductionsKind, ct).ConfigureAwait(false) ?? new List<Production>())
            .Where(p => p.RequestedAt > DateTimeOffset.UtcNow.AddDays(-90)).ToList();
        all.Add(production);
        await this.WriteTenantDocumentAsync(tenant.Id, ProductionsKind, all, ct).ConfigureAwait(false);
        byte[] sealWith = (byte[])adminData.Clone();
        _ = Task.Run(
            async () =>
            {
                using var ring = new MailKeyring();
                foreach ((MailboxRow m, UnlockedKeys k) in opened.Where(o => o.Keys.PrivateKey.Length > 0))
                {
                    ring.Add(m.Id, k.PrivateKey, k.DataKey);
                }
                try
                {
                    using (MailKeyring.Use(ring))
                    {
                        await this.BuildProductionAsync(tenant, production, opened.Select(o => o.Mailbox).ToList(), sealWith, maildirRoot, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(sealWith);
                    foreach ((MailboxRow _, UnlockedKeys k) in opened)
                    {
                        CryptographicOperations.ZeroMemory(k.PrivateKey);
                        CryptographicOperations.ZeroMemory(k.DataKey);
                    }
                }
            },
            CancellationToken.None);
        return null;
    }

    /// <summary>Make a production's file now (the background work; callable directly by tests).</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="production">The production.</param>
    /// <param name="people">Its people (their keys in the current keyring).</param>
    /// <param name="sealWith">The administrator's data key.</param>
    /// <param name="maildirRoot">Where mail files live.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The production when done.</returns>
    public async Task<Production> BuildProductionAsync(TenantRow tenant, Production production, IReadOnlyList<MailboxRow> people, byte[] sealWith, string maildirRoot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(production);
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(sealWith);
        ArgumentNullException.ThrowIfNull(maildirRoot);
        try
        {
            string folder = Path.Combine(maildirRoot, "exports", "productions");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, production.Id.ToString("N") + ".zip");
            int mails = 0;
            var index = new StringBuilder("person,folder,received,from,to,subject,message_id,file,sha256,evidence_id,evidence_sha256,evidence_captured\r\n");
            using (FileStream fs = File.Create(file))
            using (Stream target = SealedFile.Writer(fs, sealWith))
            using (var zip = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (MailboxRow person in people)
                {
                    foreach (FolderRow f in await this.store.ListFoldersAsync(person.Id, ct).ConfigureAwait(false))
                    {
                        if (string.Equals(f.Name, AttachmentPolicy.QuarantineFolder, StringComparison.Ordinal))
                        {
                            continue;
                        }
                        for (int offset = 0; ; offset += 500)
                        {
                            IReadOnlyList<MessageRow> page = await this.store.ListMessagesAsync(person.Id, f.Id, 500, offset, ct).ConfigureAwait(false);
                            foreach (MessageRow row in page.Where(r => (production.From is null || r.ReceivedAt >= production.From) && (production.To is null || r.ReceivedAt < production.To)))
                            {
                                byte[]? raw = await this.maildir.ReadAsync(tenant.Slug, person.Address, f.Name, row.MaildirFile, ct).ConfigureAwait(false);
                                if (raw is null)
                                {
                                    continue;
                                }
                                string name = SafeName(person.Address) + "/" + SafeName(f.Name) + "/" + row.ReceivedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + row.Id.ToString("N")[..8] + ".eml";
                                ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                                using (Stream es = entry.Open())
                                {
                                    await es.WriteAsync(raw, ct).ConfigureAwait(false);
                                }
                                EvidenceRow? ev = row.EvidenceId is Guid eid && this.messageStore is IEvidenceStore evidence
                                    ? await evidence.GetEvidenceAsync(eid, ct).ConfigureAwait(false)
                                    : null;
                                index.Append(Csv(person.Address)).Append(',').Append(Csv(f.Name)).Append(',')
                                    .Append(row.ReceivedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
                                    .Append(Csv(row.FromHeader)).Append(',').Append(Csv(row.ToHeader)).Append(',').Append(Csv(row.Subject)).Append(',')
                                    .Append(Csv(row.MessageId)).Append(',').Append(Csv(name)).Append(',').Append(Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant()).Append(',')
                                    .Append(ev?.Id.ToString() ?? string.Empty).Append(',').Append(ev?.Sha256 ?? string.Empty).Append(',')
                                    .Append(ev is null ? string.Empty : ev.CapturedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("\r\n");
                                mails++;
                            }
                            if (page.Count < 500)
                            {
                                break;
                            }
                        }
                    }
                }
                await WriteTextAsync(zip, "index.csv", index.ToString(), ct).ConfigureAwait(false);
                await WriteTextAsync(zip, "README.txt",
                    "Anjal production for " + tenant.DisplayName + ", made " + DateTimeOffset.UtcNow.UtcDateTime.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture) + " UTC\r\n\r\n"
                    + "Reason: " + production.Reason + "\r\n"
                    + "Period: " + (production.From?.UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture) ?? "the beginning") + " to " + (production.To?.UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture) ?? "now") + "\r\n\r\n"
                    + "<person>/<folder>/*.eml   every mail of the period, as stored (open with any mail program)\r\n"
                    + "index.csv                 one line per mail: who, folder, when, from, to, subject, Message-ID, the file, its SHA-256,\r\n"
                    + "                          and its evidence record (the untouched original's SHA-256 and when it was captured; the daily\r\n"
                    + "                          evidence manifest of that day proves it was kept unchanged)\r\n", ct).ConfigureAwait(false);
            }
            production.ReadyAt = DateTimeOffset.UtcNow;
            production.File = file;
            production.Bytes = new FileInfo(file).Length;
            production.Mails = mails;
        }
#pragma warning disable CA1031 // Whatever went wrong is recorded for the administrator, who can ask again.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            production.Error = "The download could not be made: " + ex.GetType().Name + ". Try again.";
        }
#pragma warning restore CA1031
        await this.SaveProductionAsync(tenant.Id, production, ct).ConfigureAwait(false);
        await this.messageStore.AppendAuditAsync(new AuditEvent
        {
            Actor = (await this.store.GetMailboxByIdAsync(production.By, ct).ConfigureAwait(false))?.Address ?? string.Empty,
            Action = "org.production",
            Subject = tenant.Slug,
            Detail = string.Create(CultureInfo.InvariantCulture, $"{production.Kind}: {production.People.Count} person(s), {production.Mails} mail(s), {(production.Error.Length > 0 ? "failed" : "ready")}; reason: {production.Reason}"),
        }, ct).ConfigureAwait(false);
        foreach (Guid admin in await this.AdminsOfAsync(tenant, ct).ConfigureAwait(false))
        {
            if (await this.store.GetMailboxByIdAsync(admin, ct).ConfigureAwait(false) is MailboxRow a)
            {
                await this.SendSystemMailAsync(a, a.Address, production.Kind == "leaver" ? "A leaver's mail was produced for the records" : "Mail was produced for a legal hold",
                    $"An administrator of {tenant.DisplayName} produced the mail of {production.People.Count} person(s) through the organisation's recovery key: {production.Mails} mail(s). Reason: {production.Reason}.\r\n\r\n"
                    + "Only the administrator who asked can download it, for seven days, from Organisation, Records. It is in the activity log.\r\n\r\nAnjal", ct).ConfigureAwait(false);
            }
        }
        return production;
    }

    /// <summary>The activity log line for a production's download.</summary>
    /// <param name="by">Who downloaded it.</param>
    /// <param name="production">The production.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public Task RecordProductionDownloadAsync(string by, Production production, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(production);
        return this.messageStore.AppendAuditAsync(new AuditEvent { Actor = by, Action = "org.production.downloaded", Subject = production.Id.ToString("N"), Detail = production.Reason }, ct);
    }

    private async Task SaveProductionAsync(Guid tenantId, Production production, CancellationToken ct)
    {
        List<Production> all = await this.ReadTenantDocumentAsync<List<Production>>(tenantId, ProductionsKind, ct).ConfigureAwait(false) ?? new List<Production>();
        all.RemoveAll(p => p.Id == production.Id);
        all.Add(production);
        await this.WriteTenantDocumentAsync(tenantId, ProductionsKind, all, ct).ConfigureAwait(false);
    }

    private static string Csv(string value)
    {
        string v = (value ?? string.Empty).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        // A cell starting with = + - @ would be a formula in a spreadsheet: a quote in front keeps it text.
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@')
        {
            v = "'" + v;
        }
        return "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
