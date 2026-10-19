using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>Where a person's data download stands (rc.22, item 40, DPDP), kept as their "export" document.</summary>
public sealed class ExportState
{
    /// <summary>The mailbox document kind.</summary>
    public const string Kind = "export";

    /// <summary>How long the file is kept for download.</summary>
    public static readonly TimeSpan Life = TimeSpan.FromDays(7);

    /// <summary>When it was asked for.</summary>
    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>When the file was ready; null while it is being made.</summary>
    public DateTimeOffset? ReadyAt { get; set; }

    /// <summary>The file's size.</summary>
    public long Bytes { get; set; }

    /// <summary>The file's path on the server.</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>Until when it can be downloaded.</summary>
    public DateTimeOffset? Expires { get; set; }

    /// <summary>What went wrong, or empty.</summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>Messages in the file.</summary>
    public int Messages { get; set; }

    /// <summary>True while the file is being made.</summary>
    public bool Busy => this.ReadyAt is null && this.Error.Length == 0;

    /// <summary>True when the file can be downloaded now.</summary>
    public bool Ready(DateTimeOffset now) => this.ReadyAt is not null && this.Expires is DateTimeOffset e && e > now && this.File.Length > 0;
}

/// <summary>
/// rc.22 (item 40): a person's rights under the Digital Personal Data Protection Act, 2023 -
/// download everything Anjal holds about them, in one zip, and ask for the account to be deleted.
/// </summary>
public sealed partial class MailboxService
{
    private static readonly JsonSerializerOptions ExportJson = new() { WriteIndented = true };

    /// <summary>Where a person's download stands.</summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The state, or null when none was asked for.</returns>
    public Task<ExportState?> ExportStateAsync(Guid mailboxId, CancellationToken ct = default) =>
        this.ReadDocumentAsync<ExportState>(mailboxId, ExportState.Kind, ct);

    /// <summary>
    /// Prepare the download: every message in every folder as .eml, contacts as vCard, rules,
    /// profile and settings as JSON, and the activity log about the person as CSV. Made in the
    /// background; the person is mailed a link when it is ready (within a day, usually minutes).
    /// Returns the error, or null.
    /// </summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="maildirRoot">Where mail files live; the zip goes under its exports folder.</param>
    /// <param name="baseUrl">The webmail's address, for the link.</param>
    /// <param name="ct">Cancellation (of the request only; the work goes on).</param>
    public async Task<string?> RequestExportAsync(Guid mailboxId, string maildirRoot, string baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(maildirRoot);
        ArgumentNullException.ThrowIfNull(baseUrl);
        ExportState? current = await this.ExportStateAsync(mailboxId, ct).ConfigureAwait(false);
        if (current is not null && current.Busy && current.RequestedAt > DateTimeOffset.UtcNow.AddHours(-2))
        {
            return "Your download is being prepared. You will get a mail when it is ready.";
        }
        var state = new ExportState { RequestedAt = DateTimeOffset.UtcNow };
        await this.WriteDocumentAsync(mailboxId, ExportState.Kind, state, ct).ConfigureAwait(false);
        _ = Task.Run(() => this.BuildExportAsync(mailboxId, maildirRoot, baseUrl, CancellationToken.None));
        return null;
    }

    /// <summary>Make the download now, in this call (the background job's work, callable directly by tests and tools).</summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="maildirRoot">Where mail files live.</param>
    /// <param name="baseUrl">The webmail's address, for the link.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The state when done.</returns>
    public async Task<ExportState> BuildExportAsync(Guid mailboxId, string maildirRoot, string baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(maildirRoot);
        ArgumentNullException.ThrowIfNull(baseUrl);
        ExportState state = await this.ExportStateAsync(mailboxId, ct).ConfigureAwait(false) ?? new ExportState { RequestedAt = DateTimeOffset.UtcNow };
        try
        {
            (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
            if (context is null)
            {
                state.Error = "The mailbox was not found.";
                await this.WriteDocumentAsync(mailboxId, ExportState.Kind, state, ct).ConfigureAwait(false);
                return state;
            }
            (TenantRow tenant, MailboxRow mailbox) = context.Value;
            string folder = Path.Combine(maildirRoot, "exports", mailboxId.ToString("N"));
            Directory.CreateDirectory(folder);
            foreach (string old in Directory.GetFiles(folder, "*.zip"))
            {
                File.Delete(old);
            }
            string file = Path.Combine(folder, "anjal-export-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".zip");
            int messages = 0;
            using (FileStream fs = File.Create(file))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (FolderRow f in await this.store.ListFoldersAsync(mailboxId, ct).ConfigureAwait(false))
                {
                    if (string.Equals(f.Name, Anjal.Mailbox.AttachmentPolicy.QuarantineFolder, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    for (int offset = 0; ; offset += 500)
                    {
                        IReadOnlyList<MessageRow> page = await this.store.ListMessagesAsync(mailboxId, f.Id, 500, offset, ct).ConfigureAwait(false);
                        foreach (MessageRow row in page)
                        {
                            byte[]? raw = await this.maildir.ReadAsync(tenant.Slug, mailbox.Address, f.Name, row.MaildirFile, ct).ConfigureAwait(false);
                            if (raw is null)
                            {
                                continue;
                            }
                            string name = "mail/" + SafeName(f.Name) + "/" + row.ReceivedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + row.Id.ToString("N")[..8] + ".eml";
                            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                            using (Stream es = entry.Open())
                            {
                                await es.WriteAsync(raw, ct).ConfigureAwait(false);
                            }
                            messages++;
                        }
                        if (page.Count < 500)
                        {
                            break;
                        }
                    }
                }
                await WriteTextAsync(zip, "contacts.vcf", ContactsToVCard(await this.ListContactsAsync(mailboxId, ct).ConfigureAwait(false)), ct).ConfigureAwait(false);
                await WriteTextAsync(zip, "rules.json", await this.store.GetMailboxDocumentAsync(mailboxId, Anjal.Mailbox.MailRules.Kind, ct).ConfigureAwait(false) ?? "[]", ct).ConfigureAwait(false);
                await WriteTextAsync(zip, "profile.json", JsonSerializer.Serialize(new
                {
                    mailbox.Address,
                    NameOnMails = mailbox.DisplayName,
                    Profile = await this.ProfileOfAsync(mailboxId, ct).ConfigureAwait(false),
                    Settings = MailboxPreferences.Of(mailbox),
                    Signature = await this.store.GetSignatureAsync(mailboxId, ct).ConfigureAwait(false),
                    Away = await Anjal.Mailbox.AwayReply.ReadAsync(this.store, mailboxId, ct).ConfigureAwait(false),
                    Exported = DateTimeOffset.UtcNow,
                }, ExportJson), ct).ConfigureAwait(false);
                await WriteTextAsync(zip, "activity.csv", await this.ActivityCsvAsync(mailbox.Address, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                await WriteTextAsync(zip, "README.txt",
                    "Your data from Anjal, " + DateTimeOffset.UtcNow.UtcDateTime.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture) + " UTC\r\n\r\n"
                    + "mail/<folder>/*.eml  every message, as received or sent (open with any mail program)\r\n"
                    + "contacts.vcf         your contacts (vCard)\r\n"
                    + "rules.json           your rules\r\n"
                    + "profile.json         your profile, settings, signature and away reply\r\n"
                    + "activity.csv         the activity log entries about you (sign-ins and account changes)\r\n\r\n"
                    + "Evidence copies kept under the legal rule are not included: they are the same messages, kept unchanged for the period your organisation sets.\r\n", ct).ConfigureAwait(false);
            }
            state.ReadyAt = DateTimeOffset.UtcNow;
            state.Expires = state.ReadyAt + ExportState.Life;
            state.File = file;
            state.Bytes = new FileInfo(file).Length;
            state.Messages = messages;
            state.Error = string.Empty;
            await this.WriteDocumentAsync(mailboxId, ExportState.Kind, state, ct).ConfigureAwait(false);
            string link = baseUrl.TrimEnd('/') + "/settings/data";
            await this.SendSystemMailAsync(mailbox, mailbox.Address, "Your data is ready to download",
                $"The copy of your data you asked for is ready: {messages} messages, your contacts, rules, profile and activity log, {Sizes.Text(state.Bytes)} in one zip.\r\n\r\n"
                + $"Download it from Settings, Your data, until {state.Expires.Value.UtcDateTime:d MMM yyyy} (UTC):\r\n\r\n    {link}\r\n\r\nAnjal", ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Whatever went wrong is recorded for the person, who can ask again.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            state.Error = "The download could not be made: " + ex.GetType().Name + ". Try again later, or ask your administrator.";
            await this.WriteDocumentAsync(mailboxId, ExportState.Kind, state, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning restore CA1031
        return state;
    }

    /// <summary>Remove downloads whose seven days are over (hourly).</summary>
    /// <param name="maildirRoot">Where mail files live.</param>
    /// <param name="now">Now.</param>
    /// <returns>How many files were removed.</returns>
    public static int ClearOldExports(string maildirRoot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(maildirRoot);
        string exports = Path.Combine(maildirRoot, "exports");
        if (!Directory.Exists(exports))
        {
            return 0;
        }
        int removed = 0;
        foreach (string file in Directory.GetFiles(exports, "*.zip", SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < now.UtcDateTime - ExportState.Life)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch (IOException)
            {
                // Next hour.
            }
        }
        return removed;
    }

    private async Task<string> ActivityCsvAsync(string address, CancellationToken ct)
    {
        var sb = new StringBuilder("time,action,on,from,detail\r\n");
        DateTimeOffset? before = null;
        for (int page = 0; page < 20; page++)
        {
            IReadOnlyList<AuditEvent> batch = await this.messageStore.ListAuditAsync(1000, before, ct).ConfigureAwait(false);
            foreach (AuditEvent e in batch)
            {
                if (string.Equals(e.Actor, address, StringComparison.OrdinalIgnoreCase) || string.Equals(e.Subject, address, StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append(e.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
                        .Append(CsvCell(e.Action)).Append(',').Append(CsvCell(e.Subject)).Append(',').Append(CsvCell(e.RemoteAddress)).Append(',').Append(CsvCell(e.Detail)).Append("\r\n");
                }
            }
            if (batch.Count < 1000)
            {
                break;
            }
            before = batch[^1].At;
        }
        return sb.ToString();
    }

    private static async Task WriteTextAsync(ZipArchive zip, string name, string text, CancellationToken ct)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using Stream es = entry.Open();
        await es.WriteAsync(Encoding.UTF8.GetBytes(text), ct).ConfigureAwait(false);
    }

    private static string SafeName(string folder)
    {
        var sb = new StringBuilder();
        foreach (char c in folder)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        }
        return sb.Length == 0 ? "folder" : sb.ToString();
    }
}
