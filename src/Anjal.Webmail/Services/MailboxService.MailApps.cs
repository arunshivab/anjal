using System.IO.Compression;
using System.Text;
using Anjal.Mailbox;
using Anjal.Mime;
using Anjal.Store;
using Microsoft.AspNetCore.DataProtection;

namespace Anjal.Webmail.Services;

/// <summary>A new app password, shown once.</summary>
/// <param name="Password">The password, or empty when refused.</param>
/// <param name="Error">Why not, or null.</param>
public sealed record NewAppPassword(string Password, string? Error);

/// <summary>rc.28 (DES-20): mail apps (a password per device) and bringing old mail in.</summary>
public sealed partial class MailboxService
{
    /// <summary>The largest file taken for import (D-346).</summary>
    public const long MaxImportBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>A job waiting for the person to choose folders (a copy from another server).</summary>
    public const string ImportChoosing = "choosing";

    /// <summary>The folder imported mail goes to when none is chosen.</summary>
    public const string ImportedFolder = "Imported";

    private IMailAppStore? MailApps => this.store as IMailAppStore;

    /// <summary>A person's app passwords that work, newest first.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The app passwords.</returns>
    public async Task<IReadOnlyList<AppPasswordRow>> AppPasswordsAsync(Guid mailboxId, CancellationToken ct = default) =>
        this.MailApps is IMailAppStore apps ? await apps.ListAppPasswordsAsync(mailboxId, ct).ConfigureAwait(false) : Array.Empty<AppPasswordRow>();

    /// <summary>
    /// Make an app password (DES-20 §2). The person is signed in, so their mail key is open in this request;
    /// a copy is wrapped with the new password. The password is returned once and never kept.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="name">The device's name.</param>
    /// <param name="actor">Who.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The password, or why not.</returns>
    public async Task<NewAppPassword> MakeAppPasswordAsync(Guid mailboxId, string? name, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string device = (name ?? string.Empty).Trim();
        if (this.MailApps is not IMailAppStore apps || await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false) is not (TenantRow _, MailboxRow mailbox))
        {
            return new NewAppPassword(string.Empty, "Mail apps are not available here.");
        }
        if (device.Length is 0 or > AppPasswords.MaxName)
        {
            return new NewAppPassword(string.Empty, "Give the device a name, up to 60 characters, such as \"My phone\".");
        }
        IReadOnlyList<AppPasswordRow> existing = await apps.ListAppPasswordsAsync(mailboxId, ct).ConfigureAwait(false);
        if (existing.Count >= AppPasswords.MaxPerPerson)
        {
            return new NewAppPassword(string.Empty, "You have 10 mail apps already. Remove one you no longer use first.");
        }
        byte[]? privateKey = null;
        if (this.KeyManager is MailKeyManager km && await km.Store.GetMailboxKeysAsync(mailboxId, ct).ConfigureAwait(false) is not null)
        {
            privateKey = MailKeyring.Current.PrivateKeyOf(mailboxId);
            if (privateKey is null)
            {
                return new NewAppPassword(string.Empty, "Your mail is locked in this session. Sign out and sign in again, then make the app password.");
            }
        }
        string password = AppPasswords.NewPassword();
        await apps.AddAppPasswordAsync(AppPasswords.Make(mailbox.Id, device, password, privateKey, MailKeys.PepperFromEnvironment(), DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
        await this.messageStore.AppendAuditAsync(new AuditEvent { Actor = actor, Action = "mailapp.made", Subject = mailbox.Address, Detail = device }, ct).ConfigureAwait(false);
        return new NewAppPassword(password, null);
    }

    /// <summary>Remove an app password: the app stops at once and its key copy is deleted.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="id">The app password.</param>
    /// <param name="actor">Who.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when removed.</returns>
    public async Task<bool> RemoveAppPasswordAsync(Guid mailboxId, Guid id, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (this.MailApps is not IMailAppStore apps)
        {
            return false;
        }
        string name = (await apps.ListAppPasswordsAsync(mailboxId, ct).ConfigureAwait(false)).FirstOrDefault(a => a.Id == id)?.Name ?? string.Empty;
        if (!await apps.RemoveAppPasswordAsync(mailboxId, id, DateTimeOffset.UtcNow, ct).ConfigureAwait(false))
        {
            return false;
        }
        await this.messageStore.AppendAuditAsync(new AuditEvent { Actor = actor, Action = "mailapp.removed", Subject = actor, Detail = name }, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>A person's import jobs, newest first.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The jobs.</returns>
    public async Task<IReadOnlyList<ImportJobRow>> ImportJobsAsync(Guid mailboxId, CancellationToken ct = default) =>
        this.MailApps is IMailAppStore apps ? await apps.ListImportJobsAsync(mailboxId, ct).ConfigureAwait(false) : Array.Empty<ImportJobRow>();

    /// <summary>Take an uploaded file for import (DES-20 §5): kept on the server's disk until its job is done.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="upload">The file's bytes.</param>
    /// <param name="fileName">Its name.</param>
    /// <param name="target">The folder to put the mail in; empty for "Imported".</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Why not, or null.</returns>
    public async Task<string?> StartFileImportAsync(Guid mailboxId, Stream upload, string fileName, string? target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(fileName);
        if (this.MailApps is not IMailAppStore apps || await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false) is not (TenantRow _, MailboxRow _))
        {
            return "Import is not available here.";
        }
        string folder = (target ?? string.Empty).Trim();
        if (folder.Length > 0 && !FolderRow.IsValidName(folder))
        {
            return "Folder names are 1 to 100 characters, without / or \\.";
        }
        Guid id = Guid.NewGuid();
        string dir = Path.Combine(ImportRoot, mailboxId.ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, id.ToString("N") + ".upload");
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await upload.CopyToAsync(file, ct).ConfigureAwait(false);
        }
        await apps.AddImportJobAsync(new ImportJobRow
        {
            Id = id,
            MailboxId = mailboxId,
            Kind = ImportJobRow.FileKind,
            Source = path,
            Folders = Path.GetFileName(fileName),
            TargetFolder = folder,
            State = ImportJobRow.Waiting,
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// "Look at the account": sign in to the other server and list its folders, so the person can tick which
    /// to copy (DES-20 §5). The password is kept sealed with the webmail's own key only until the copy ends.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="host">The other server.</param>
    /// <param name="user">The account there.</param>
    /// <param name="password">Its password (an app password made there).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The job, or why not.</returns>
    public async Task<(Guid? Job, string? Error)> LookAtServerAsync(Guid mailboxId, string? host, string? user, string? password, CancellationToken ct = default)
    {
        string h = (host ?? string.Empty).Trim().ToLowerInvariant();
        string u = (user ?? string.Empty).Trim();
        if (this.MailApps is not IMailAppStore apps || this.Protector is null)
        {
            return (null, "Import is not available here.");
        }
        if (h.Length == 0 || h.Length > 253 || h.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) || u.Length == 0 || string.IsNullOrEmpty(password))
        {
            return (null, "Give the server's name (such as imap.gmail.com), the account and its password.");
        }
        IReadOnlyList<string> folders;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(45));
            await using Anjal.Imap.ImapPullClient client = await Anjal.Imap.ImapPullClient.ConnectAsync(h, 993, tls: true, limit.Token).ConfigureAwait(false);
            if (await client.LoginAsync(u, password, limit.Token).ConfigureAwait(false) is string refused)
            {
                return (null, "The other server refused the sign-in. Many need an app password made there for this.");
            }
            folders = await client.ListAsync(limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        {
            return (null, "Anjal could not reach that server on port 993. Check its name.");
        }
        ImportJobRow job = await apps.AddImportJobAsync(new ImportJobRow
        {
            MailboxId = mailboxId,
            Kind = ImportJobRow.ServerKind,
            Source = h + ":993 " + u,
            Secret = this.Protector.Protect(password),
            Folders = string.Join('\n', folders),
            State = ImportChoosing,
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);
        return (job.Id, null);
    }

    /// <summary>The person ticked the folders to copy: the job starts.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="jobId">The job.</param>
    /// <param name="chosen">The folders.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Why not, or null.</returns>
    public async Task<string?> ChooseImportFoldersAsync(Guid mailboxId, Guid jobId, IReadOnlyCollection<string> chosen, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        if (this.MailApps is not IMailAppStore apps || await apps.GetImportJobAsync(jobId, ct).ConfigureAwait(false) is not ImportJobRow job || job.MailboxId != mailboxId || job.State != ImportChoosing)
        {
            return "This import is no longer waiting for folders.";
        }
        var offered = job.Folders.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var picked = chosen.Where(offered.Contains).Distinct(StringComparer.Ordinal).ToList();
        if (picked.Count == 0)
        {
            return "Tick at least one folder.";
        }
        job.Folders = string.Join('\n', picked);
        job.State = ImportJobRow.Waiting;
        await apps.SaveImportJobAsync(job, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Stop an import; what came in stays.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="jobId">The job.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when stopped.</returns>
    public async Task<bool> StopImportAsync(Guid mailboxId, Guid jobId, CancellationToken ct = default)
    {
        if (this.MailApps is not IMailAppStore apps || await apps.GetImportJobAsync(jobId, ct).ConfigureAwait(false) is not ImportJobRow job || job.MailboxId != mailboxId
            || job.State is ImportJobRow.Done or ImportJobRow.Failed or ImportJobRow.Stopped)
        {
            return false;
        }
        job.State = ImportJobRow.Stopped;
        job.Secret = string.Empty;
        await apps.SaveImportJobAsync(job, ct).ConfigureAwait(false);
        DeleteUpload(job);
        return true;
    }

    /// <summary>Run every waiting import, one after another (the import worker calls this).</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many jobs ran.</returns>
    public async Task<int> RunImportsAsync(CancellationToken ct = default)
    {
        if (this.MailApps is not IMailAppStore apps)
        {
            return 0;
        }
        // A look at another server whose folders were never chosen ends after an hour, its password with it.
        await apps.ExpireChoosingImportJobsAsync(DateTimeOffset.UtcNow.AddHours(-1), ct).ConfigureAwait(false);
        int ran = 0;
        foreach (ImportJobRow open in await apps.ListOpenImportJobsAsync(ct).ConfigureAwait(false))
        {
            // One runner per job: a second worker (or a test beside the worker) leaves it alone.
            if (!await apps.ClaimImportJobAsync(open.Id, DateTimeOffset.UtcNow.AddMinutes(-10), ct).ConfigureAwait(false)
                || await apps.GetImportJobAsync(open.Id, ct).ConfigureAwait(false) is not ImportJobRow job)
            {
                continue;
            }
            await this.RunImportAsync(apps, job, ct).ConfigureAwait(false);
            ran++;
        }
        return ran;
    }

    // Beside the mail, where both the webmail and its import worker can reach it.
    private string ImportRoot => Path.Combine((this.maildir as MaildirStore)?.Root ?? MaildirStore.DefaultRoot, ".imports");

    private static void DeleteUpload(ImportJobRow job)
    {
        if (job.Kind == ImportJobRow.FileKind && job.Source.EndsWith(".upload", StringComparison.Ordinal) && File.Exists(job.Source))
        {
            File.Delete(job.Source);
        }
    }

    private async Task RunImportAsync(IMailAppStore apps, ImportJobRow job, CancellationToken ct)
    {
        if (await this.GetContextAsync(job.MailboxId, ct).ConfigureAwait(false) is not (TenantRow tenant, MailboxRow mailbox))
        {
            job.State = ImportJobRow.Failed;
            job.Error = "The mailbox is gone.";
            await apps.SaveImportJobAsync(job, ct).ConfigureAwait(false);
            return;
        }
        try
        {
            string? error = job.Kind == ImportJobRow.FileKind
                ? await this.ImportFileAsync(apps, job, tenant, mailbox, ct).ConfigureAwait(false)
                : await this.ImportServerAsync(apps, job, tenant, mailbox, ct).ConfigureAwait(false);
            if (await apps.GetImportJobAsync(job.Id, ct).ConfigureAwait(false) is { State: ImportJobRow.Stopped })
            {
                return;
            }
            job.State = error is null ? ImportJobRow.Done : ImportJobRow.Failed;
            job.Error = error ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException)
        {
            job.State = ImportJobRow.Failed;
            job.Error = ex is InvalidDataException ? "The file could not be read." : "The other server stopped answering. Start the import again to go on from where it stopped.";
        }
        job.Secret = string.Empty;
        await apps.SaveImportJobAsync(job, ct).ConfigureAwait(false);
        DeleteUpload(job);
        string text = "Your mail import has " + (job.State == ImportJobRow.Done ? "finished" : "stopped") + ": "
            + job.Imported.ToString(System.Globalization.CultureInfo.InvariantCulture) + " message(s) brought in, "
            + job.Skipped.ToString(System.Globalization.CultureInfo.InvariantCulture) + " skipped (already here, too large, or not safe)"
            + (job.Error.Length > 0 ? ". " + job.Error : ".") + "\n\nSettings, Mail apps shows each import";
        await this.SendSystemMailAsync(mailbox, mailbox.Address, "Your mail import has " + (job.State == ImportJobRow.Done ? "finished" : "stopped"), text, ct).ConfigureAwait(false);
    }

    private async Task<string?> ImportFileAsync(IMailAppStore apps, ImportJobRow job, TenantRow tenant, MailboxRow mailbox, CancellationToken ct)
    {
        if (!File.Exists(job.Source))
        {
            return "The uploaded file is gone. Upload it again.";
        }
        int done = int.TryParse(job.Cursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int c) ? c : 0;
        string folderName = job.TargetFolder.Length > 0 ? job.TargetFolder : ImportedFolder;
        FolderRow folder = await this.store.EnsureFolderAsync(mailbox.Id, folderName, ct).ConfigureAwait(false);
        HashSet<string> already = await this.MessageIdsInAsync(mailbox.Id, folder.Id, ct).ConfigureAwait(false);
        int index = 0;
        foreach (byte[] raw in MailImportFile.Read(job.Source))
        {
            index++;
            if (index <= done)
            {
                continue;
            }
            job.Imported = job.Seen - job.Skipped;
            job.Seen++;
            (string? stop, bool kept) = await this.ImportOneAsync(tenant, mailbox, folder, raw, null, seen: true, flagged: false, already, ct).ConfigureAwait(false);
            if (stop is not null)
            {
                return stop;
            }
            job.Skipped += kept ? 0 : 1;
            if (job.Seen % 50 == 0)
            {
                job.Cursor = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await apps.SaveImportJobAsync(job, ct).ConfigureAwait(false);
                if (await apps.GetImportJobAsync(job.Id, ct).ConfigureAwait(false) is { State: ImportJobRow.Stopped })
                {
                    return null;
                }
            }
            job.Imported = job.Seen - job.Skipped;
        }
        job.Cursor = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        job.Total = index;
        return null;
    }

    private async Task<string?> ImportServerAsync(IMailAppStore apps, ImportJobRow job, TenantRow tenant, MailboxRow mailbox, CancellationToken ct)
    {
        if (this.Protector is null || job.Secret.Length == 0)
        {
            return "The password is no longer kept. Start the import again.";
        }
        string password;
        try
        {
            password = this.Protector.Unprotect(job.Secret);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return "The password is no longer kept. Start the import again.";
        }
        string hostPort = job.Source.Split(' ')[0];
        string user = job.Source[(job.Source.IndexOf(' ', StringComparison.Ordinal) + 1)..];
        string host = hostPort[..hostPort.LastIndexOf(':')];
        int port = int.Parse(hostPort[(hostPort.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var cursor = job.Cursor.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')).Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => long.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
        await using Anjal.Imap.ImapPullClient client = await Anjal.Imap.ImapPullClient.ConnectAsync(host, port, tls: !TestPlainImport, ct).ConfigureAwait(false);
        if (await client.LoginAsync(user, password, ct).ConfigureAwait(false) is not null)
        {
            return "The other server refused the sign-in.";
        }
        int n = 0;
        foreach (string remoteFolder in job.Folders.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            n++;
            long after = cursor.TryGetValue(remoteFolder, out long last) ? last : 0;
            IReadOnlyList<long>? uids = await client.UidsAsync(remoteFolder, after, ct).ConfigureAwait(false);
            if (uids is null)
            {
                continue;
            }
            job.Total += uids.Count;
            FolderRow folder = await this.store.EnsureFolderAsync(mailbox.Id, ImportFolderName(remoteFolder, n), ct).ConfigureAwait(false);
            HashSet<string> already = await this.MessageIdsInAsync(mailbox.Id, folder.Id, ct).ConfigureAwait(false);
            foreach (long[] batch in uids.Chunk(50))
            {
                foreach (Anjal.Imap.PulledMessage m in await client.FetchAsync(batch, ct).ConfigureAwait(false))
                {
                    job.Seen++;
                    (string? stop, bool kept) = await this.ImportOneAsync(tenant, mailbox, folder, m.Raw, m.Received, m.Seen, m.Flagged, already, ct).ConfigureAwait(false);
                    if (stop is not null)
                    {
                        return stop;
                    }
                    job.Skipped += kept ? 0 : 1;
                    job.Imported = job.Seen - job.Skipped;
                    cursor[remoteFolder] = Math.Max(m.Uid, cursor.GetValueOrDefault(remoteFolder));
                }
                job.Cursor = string.Join('\n', cursor.Select(kv => kv.Key + "\t" + kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                await apps.SaveImportJobAsync(job, ct).ConfigureAwait(false);
                if (await apps.GetImportJobAsync(job.Id, ct).ConfigureAwait(false) is { State: ImportJobRow.Stopped })
                {
                    return null;
                }
            }
        }
        return null;
    }

    /// <summary>For tests only: the copy from another server talks to a local test server without TLS.</summary>
    public static bool TestPlainImport { get; set; }

    /// <summary>The folder an old folder's mail goes to: "Imported - Sent Mail" (Anjal's folder names are plain letters, so others become underscores).</summary>
    /// <param name="remote">The old folder's name.</param>
    /// <param name="n">Its place in the list, for a name with no plain letters.</param>
    /// <returns>The folder's name.</returns>
    public static string ImportFolderName(string remote, int n)
    {
        string plain = new(remote.Select(ch => ch is >= ' ' and <= '~' && ch is not '/' and not '\\' ? ch : '_').ToArray());
        plain = plain.Trim().TrimStart('.', '[').Replace("]", string.Empty, StringComparison.Ordinal).Trim();
        string name = "Imported - " + (plain.Trim('_', ' ').Length > 0 ? plain : "folder " + n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return name.Length > 100 ? name[..100].TrimEnd() : name;
    }

    private async Task<HashSet<string>> MessageIdsInAsync(Guid mailboxId, Guid folderId, CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int offset = 0; ; offset += 1000)
        {
            IReadOnlyList<MessageRow> page = await this.store.ListMessagesAsync(mailboxId, folderId, 1000, offset, ct).ConfigureAwait(false);
            foreach (MessageRow r in page)
            {
                if (r.MessageId.Length > 0)
                {
                    ids.Add(r.MessageId);
                }
            }
            if (page.Count < 1000)
            {
                return ids;
            }
        }
    }

    // One message in: checked like mail from outside, kept with its own date, never through rules or replies.
    // Stop says why the job must stop (the mailbox is full); Kept is false for a message skipped.
    private async Task<(string? Stop, bool Kept)> ImportOneAsync(TenantRow tenant, MailboxRow mailbox, FolderRow folder, byte[] raw, DateTimeOffset? received, bool seen, bool flagged, HashSet<string> already, CancellationToken ct)
    {
        if (raw.Length == 0 || raw.Length > Anjal.Imap.ImapPullClient.MaxMessage)
        {
            return (null, false);
        }
        MimeMessage? parsed = TryParse(raw);
        string messageId = parsed?.MessageId ?? string.Empty;
        if ((messageId.Length > 0 && !already.Add(messageId)) || AttachmentPolicy.RefusedInside(parsed) is not null)
        {
            return (null, false);
        }
        if (this.Scanner is IVirusScanner scanner && (await scanner.ScanAsync(raw, ct).ConfigureAwait(false)).Verdict == ScanVerdict.Infected)
        {
            return (null, false);
        }
        if ((await StoragePlan.StateOfAsync(this.store, mailbox, ct).ConfigureAwait(false)).Full)
        {
            return ("Your mailbox is full. Delete or archive some mail, then start the import again.", false);
        }
        DateTimeOffset when = received ?? HeaderDate(parsed?.Date) ?? DateTimeOffset.UtcNow;
        MaildirWriteResult written = await this.maildir.WriteAsync(tenant.Slug, mailbox.Address, folder.Name, raw, ct).ConfigureAwait(false);
        string? path = this.maildir.SetFlags(tenant.Slug, mailbox.Address, folder.Name, written.RelativePath, seen, flagged, answered: false);
        MessageRow saved = await this.store.SaveMessageAsync(new MessageRow
        {
            MailboxId = mailbox.Id,
            FolderId = folder.Id,
            MaildirFile = path ?? written.RelativePath,
            EnvelopeFrom = string.Empty,
            MessageId = messageId,
            FromHeader = parsed?.Headers.Get("From") ?? string.Empty,
            ToHeader = parsed?.Headers.Get("To") ?? string.Empty,
            Subject = parsed?.Subject ?? string.Empty,
            DateHeader = parsed?.Date ?? string.Empty,
            SizeBytes = written.SizeBytes,
            ReceivedAt = when,
            Seen = seen,
            Flagged = flagged,
            SpamChecked = false,
            HasAttachments = MailboxSink.HasAttachment(parsed?.Body),
            BodyText = MessageText.Extract(parsed),
        }, ct).ConfigureAwait(false);
        if (this.MailApps is IMailAppStore apps)
        {
            await apps.SetReceivedAtAsync(saved.Id, when, ct).ConfigureAwait(false);
        }
        await this.store.AddMailboxUsageAsync(mailbox.Id, written.SizeBytes, ct).ConfigureAwait(false);
        return (null, true);
    }

    /// <summary>A Date header's time: "Mon, 9 Nov 2026 10:00:00 +0530" (a trailing "(IST)" and the day's name are allowed).</summary>
    /// <param name="value">The header.</param>
    /// <returns>The time, or null.</returns>
    public static DateTimeOffset? HeaderDate(string? value)
    {
        string v = (value ?? string.Empty).Trim();
        int paren = v.IndexOf('(', StringComparison.Ordinal);
        if (paren > 0)
        {
            v = v[..paren].Trim();
        }
        int comma = v.IndexOf(',', StringComparison.Ordinal);
        if (comma >= 0)
        {
            v = v[(comma + 1)..].Trim();
        }
        if (v.Length > 5 && (v[^5] == '+' || v[^5] == '-') && v[^4..].All(char.IsAsciiDigit))
        {
            v = v[..^2] + ":" + v[^2..];
        }
        string[] formats = { "d MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm zzz", "d MMM yy HH:mm:ss zzz" };
        return DateTimeOffset.TryParseExact(v, formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset d) ? d : null;
    }
}

/// <summary>rc.28 (DES-20 §5): the messages in an uploaded file - a zip of .eml files, an mbox, or one .eml.</summary>
public static class MailImportFile
{
    /// <summary>Read each message, in order, without loading the whole file.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The messages.</returns>
    public static IEnumerable<byte[]> Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] head = new byte[5];
        int got;
        using (FileStream probe = File.OpenRead(path))
        {
            got = probe.Read(head, 0, head.Length);
        }
        if (got >= 4 && head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04)
        {
            return ReadZip(path);
        }
        if (got == 5 && Encoding.ASCII.GetString(head) == "From ")
        {
            return ReadMbox(path);
        }
        // One message: larger than Anjal copies, it is counted as skipped without being read.
        return new FileInfo(path).Length > Anjal.Imap.ImapPullClient.MaxMessage ? new[] { Array.Empty<byte>() } : new[] { File.ReadAllBytes(path) };
    }

    private static IEnumerable<byte[]> ReadZip(string path)
    {
        using ZipArchive zip = ZipFile.OpenRead(path);
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            if (e.FullName.EndsWith('/') || e.Length == 0 || e.Length > Anjal.Imap.ImapPullClient.MaxMessage || !e.Name.EndsWith(".eml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            using Stream s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            yield return ms.ToArray();
        }
    }

    // mbox (mboxrd): a message starts at a "From " line; ">From " lines inside lose one ">".
    private static IEnumerable<byte[]> ReadMbox(string path)
    {
        using FileStream file = File.OpenRead(path);
        var message = new MemoryStream();
        var line = new List<byte>();
        bool started = false;
        bool tooLarge = false;
        int b;
        while ((b = file.ReadByte()) >= 0)
        {
            line.Add((byte)b);
            if (b != 10)
            {
                // A line with no end (or a message past the limit) is not kept; the message it is in
                // is passed over (counted as skipped) up to the next "From " line.
                if (line.Count > 1024 * 1024)
                {
                    line.Clear();
                    tooLarge = true;
                }
                continue;
            }
            byte[] l = line.ToArray();
            line.Clear();
            if (l.Length >= 5 && l[0] == (byte)'F' && Encoding.ASCII.GetString(l, 0, 5) == "From ")
            {
                if (started && (message.Length > 0 || tooLarge))
                {
                    yield return tooLarge ? Array.Empty<byte>() : Trim(message.ToArray());
                }
                message.SetLength(0);
                started = true;
                tooLarge = false;
                continue;
            }
            if (tooLarge)
            {
                continue;
            }
            int gt = 0;
            while (gt < l.Length && l[gt] == (byte)'>')
            {
                gt++;
            }
            int skip = gt > 0 && l.Length >= gt + 5 && Encoding.ASCII.GetString(l, gt, 5) == "From " ? 1 : 0;
            int len = l.Length - skip;
            // Lines end CRLF, as mail does.
            bool lf = len >= 1 && l[^1] == 10 && (len < 2 || l[^2] != 13);
            message.Write(l, skip, lf ? len - 1 : len);
            if (lf)
            {
                message.WriteByte(13);
                message.WriteByte(10);
            }
            if (message.Length > Anjal.Imap.ImapPullClient.MaxMessage)
            {
                message.SetLength(0);
                tooLarge = true;
            }
        }
        if (line.Count > 0 && !tooLarge)
        {
            message.Write(line.ToArray());
        }
        if (started && (message.Length > 0 || tooLarge))
        {
            yield return tooLarge ? Array.Empty<byte>() : Trim(message.ToArray());
        }
        message.Dispose();
    }

    // The blank line before the next "From " belongs to the mbox, not the message.
    private static byte[] Trim(byte[] m)
    {
        int end = m.Length;
        if (end >= 4 && m[end - 4] == 13 && m[end - 3] == 10 && m[end - 2] == 13 && m[end - 1] == 10)
        {
            end -= 2;
        }
        return end == m.Length ? m : m[..end];
    }
}
