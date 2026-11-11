using Anjal.Mailbox;
using Anjal.Store;

namespace Anjal.Imap;

/// <summary>
/// rc.28 (DES-20): the IMAP server's view of Anjal's own store - people sign in with app passwords only,
/// their keys open from the app's own copy, and every change goes through the same index and Maildir the
/// webmail uses.
/// </summary>
public sealed class StoreImapBackend : IImapBackend
{
    /// <summary>What an app is told when it sends the account password.</summary>
    public const string UseAppPassword = "Use a password made for this app under Settings, Mail apps.";

    /// <summary>Folders the webmail keeps to itself.</summary>
    internal static readonly IReadOnlySet<string> Hidden = new HashSet<string>(new[] { "Scheduled", "Snoozed", "Outbox", AttachmentPolicy.QuarantineFolder }, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> SpecialUse = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Sent"] = "\\Sent",
        ["Drafts"] = "\\Drafts",
        ["Trash"] = "\\Trash",
        [MailboxSink.JunkFolder] = "\\Junk",
        ["Archive"] = "\\Archive",
    };

    private readonly IMailboxStore store;
    private readonly IMailAppStore apps;
    private readonly IMessageStore records;
    private readonly MaildirStore maildir;
    private readonly byte[] pepper;
    private readonly Action<string>? log;

    /// <summary>Construct.</summary>
    /// <param name="store">The mailbox index (also the app-password, key and audit store).</param>
    /// <param name="maildir">The Maildir.</param>
    /// <param name="pepper">The pepper.</param>
    /// <param name="log">Optional log.</param>
    public StoreImapBackend(IMailboxStore store, MaildirStore maildir, byte[] pepper, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(maildir);
        ArgumentNullException.ThrowIfNull(pepper);
        this.store = store;
        this.apps = store as IMailAppStore ?? throw new ArgumentException("The store keeps no app passwords.", nameof(store));
        this.records = store as IMessageStore ?? throw new ArgumentException("The store keeps no activity log.", nameof(store));
        this.maildir = maildir;
        this.pepper = pepper;
        this.log = log;
    }

    /// <inheritdoc/>
    public async Task<ImapLogin> LoginAsync(string user, string password, string remoteAddress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(remoteAddress);
        int at = user.LastIndexOf('@');
        MailboxRow? mailbox = at > 0 ? await this.store.GetMailboxAsync(user[..at].Trim().ToLowerInvariant(), user[(at + 1)..].Trim().ToLowerInvariant(), ct).ConfigureAwait(false) : null;
        // As submission: a switched-off mailbox or organisation cannot sign in (a leaver's app stops too).
        if (mailbox is null || !mailbox.Enabled || await this.store.GetTenantByIdAsync(mailbox.TenantId, ct).ConfigureAwait(false) is not TenantRow tenant || !tenant.Enabled)
        {
            Anjal.Smtp.Pbkdf2Hasher.VerifyAgainstDummy(password);
            return new ImapLogin(null, null);
        }
        IReadOnlyList<AppPasswordRow> rows = await this.apps.ListAppPasswordsAsync(mailbox.Id, ct).ConfigureAwait(false);
        AppPasswordRow? app = AppPasswords.Match(rows, password);
        if (app is null)
        {
            // The account password works nowhere but the webmail (DES-20 §2): say so, once it is known to be right.
            bool account = !AppPasswords.LooksLikeOne(password) && mailbox.PasswordPbkdf2.Length > 0 && Anjal.Smtp.Pbkdf2Hasher.Verify(password, mailbox.PasswordPbkdf2);
            await this.AuditAsync(mailbox.Address, "imap.sign-in-refused", account ? "the account password" : "a wrong password", remoteAddress, ct).ConfigureAwait(false);
            return new ImapLogin(null, account ? UseAppPassword : null);
        }
        MailboxKeyRow? keys = this.store is IMailKeyStore keyStore ? await keyStore.GetMailboxKeysAsync(mailbox.Id, ct).ConfigureAwait(false) : null;
        MailKeyring? ring = AppPasswords.OpenKeyring(keys, app, password, this.pepper);
        if (ring is null)
        {
            await this.AuditAsync(mailbox.Address, "imap.sign-in-refused", "the app's key copy did not open", remoteAddress, ct).ConfigureAwait(false);
            return new ImapLogin(null, "This app password no longer opens your mail. Remove it and make a new one under Settings, Mail apps.");
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await this.apps.TouchAppPasswordAsync(app.Id, now, remoteAddress, ct).ConfigureAwait(false);
        await this.AuditAsync(mailbox.Address, "imap.sign-in", app.Name, remoteAddress, ct).ConfigureAwait(false);
        this.log?.Invoke($"IMAP: {mailbox.Address} signed in ({app.Name}) from {remoteAddress}");
        return new ImapLogin(new Account(this, tenant, mailbox, ring, app.Id), null);
    }

    private Task<AuditEvent> AuditAsync(string actor, string action, string detail, string remote, CancellationToken ct) =>
        this.records.AppendAuditAsync(new AuditEvent { Actor = actor, Action = action, Subject = actor, Detail = detail, RemoteAddress = remote }, ct);

    private sealed class Account(StoreImapBackend owner, TenantRow tenant, MailboxRow mailbox, MailKeyring ring, Guid appId) : IImapAccount
    {
        private static readonly TimeSpan LookAgain = TimeSpan.FromSeconds(30);
        private DateTimeOffset lookedAt = DateTimeOffset.UtcNow;
        private bool allowed = true;

        public string Address => mailbox.Address;

        public async Task<bool> StillAllowedAsync(CancellationToken ct = default)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!this.allowed || now - this.lookedAt < LookAgain)
            {
                return this.allowed;
            }
            this.lookedAt = now;
            MailboxRow? box = await owner.store.GetMailboxByIdAsync(mailbox.Id, ct).ConfigureAwait(false);
            TenantRow? org = await owner.store.GetTenantByIdAsync(tenant.Id, ct).ConfigureAwait(false);
            IReadOnlyList<AppPasswordRow> rows = await owner.apps.ListAppPasswordsAsync(mailbox.Id, ct).ConfigureAwait(false);
            this.allowed = box is { Enabled: true } && org is { Enabled: true } && rows.Any(r => r.Id == appId && r.RemovedAt is null);
            return this.allowed;
        }

        public void Dispose() => ring.Dispose();

        public async Task<IReadOnlyList<ImapFolder>> FoldersAsync(CancellationToken ct = default)
        {
            using IDisposable keys = MailKeyring.Use(ring);
            foreach (string name in new[] { FolderRow.Inbox, "Sent", "Drafts", "Trash", MailboxSink.JunkFolder, "Archive" })
            {
                await owner.store.EnsureFolderAsync(mailbox.Id, name, ct).ConfigureAwait(false);
            }
            return (await owner.store.ListFoldersAsync(mailbox.Id, ct).ConfigureAwait(false))
                .Where(f => !Hidden.Contains(f.Name))
                .OrderBy(f => f.Name == FolderRow.Inbox ? 0 : SpecialUse.ContainsKey(f.Name) ? 1 : 2).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new ImapFolder(f.Name, SpecialUse.TryGetValue(f.Name, out string? s) ? s : string.Empty)).ToList();
        }

        public async Task<ImapView?> OpenAsync(string name, CancellationToken ct = default)
        {
            using IDisposable keys = MailKeyring.Use(ring);
            if (await this.FolderAsync(name, ct).ConfigureAwait(false) is not FolderRow folder)
            {
                return null;
            }
            ImapFolderNumbers numbers = await owner.apps.AssignImapUidsAsync(folder.Id, ct).ConfigureAwait(false);
            var rows = new Dictionary<Guid, MessageRow>();
            for (int offset = 0; ; offset += 1000)
            {
                IReadOnlyList<MessageRow> page = await owner.store.ListMessagesAsync(mailbox.Id, folder.Id, 1000, offset, ct).ConfigureAwait(false);
                foreach (MessageRow r in page)
                {
                    rows[r.Id] = r;
                }
                if (page.Count < 1000)
                {
                    break;
                }
            }
            var list = new List<ImapMessage>(numbers.Uids.Count);
            foreach ((Guid id, long uid) in numbers.Uids)
            {
                if (rows.TryGetValue(id, out MessageRow? r))
                {
                    list.Add(new ImapMessage { Id = id, Uid = uid, Seen = r.Seen, Flagged = r.Flagged, Answered = r.Answered, Received = r.ReceivedAt });
                }
            }
            return new ImapView(new ImapFolder(folder.Name, SpecialUse.TryGetValue(folder.Name, out string? s) ? s : string.Empty), numbers.UidValidity, numbers.UidNext, list);
        }

        public async Task<byte[]?> ReadAsync(Guid id, CancellationToken ct = default)
        {
            using IDisposable keys = MailKeyring.Use(ring);
            if (await this.OwnedAsync(id, ct).ConfigureAwait(false) is not (MessageRow row, FolderRow folder))
            {
                return null;
            }
            try
            {
                return await owner.maildir.ReadAsync(tenant.Slug, mailbox.Address, folder.Name, row.MaildirFile, ct).ConfigureAwait(false);
            }
            catch (MailLockedException)
            {
                return null;
            }
        }

        public async Task SetFlagsAsync(Guid id, bool seen, bool flagged, bool answered, CancellationToken ct = default)
        {
            using IDisposable keys = MailKeyring.Use(ring);
            if (await this.OwnedAsync(id, ct).ConfigureAwait(false) is not (MessageRow row, FolderRow folder))
            {
                return;
            }
            string? renamed = owner.maildir.SetFlags(tenant.Slug, mailbox.Address, folder.Name, row.MaildirFile, seen, flagged, answered);
            await owner.store.SetMessageFlagsAsync(id, seen, flagged, answered, renamed ?? row.MaildirFile, ct).ConfigureAwait(false);
        }

        public async Task<Guid?> CopyAsync(Guid id, string folder, CancellationToken ct = default)
        {
            using IDisposable keys = MailKeyring.Use(ring);
            if (await this.OwnedAsync(id, ct).ConfigureAwait(false) is not (MessageRow row, FolderRow _) || await this.ReadAsync(id, ct).ConfigureAwait(false) is not byte[] raw)
            {
                return null;
            }
            (Guid? copy, string? _) = await this.AppendAsync(folder, raw, row.Seen, row.Flagged, row.Answered, row.ReceivedAt, ct).ConfigureAwait(false);
            return copy;
        }

        public async Task<bool> MoveAsync(Guid id, string folder, CancellationToken ct = default)
        {
            using IDisposable keys = MailKeyring.Use(ring);
            if (await this.OwnedAsync(id, ct).ConfigureAwait(false) is not (MessageRow row, FolderRow from) || await this.FolderAsync(folder, ct).ConfigureAwait(false) is not FolderRow to)
            {
                return false;
            }
            if (to.Id == from.Id)
            {
                return true;
            }
            string? moved = owner.maildir.Move(tenant.Slug, mailbox.Address, from.Name, row.MaildirFile, to.Name);
            return await owner.store.MoveMessageAsync(id, to.Id, moved ?? row.MaildirFile, ct).ConfigureAwait(false) is not null;
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
        {
            using IDisposable keys = MailKeyring.Use(ring);
            if (await this.OwnedAsync(id, ct).ConfigureAwait(false) is not (MessageRow row, FolderRow folder))
            {
                return false;
            }
            owner.maildir.Delete(tenant.Slug, mailbox.Address, folder.Name, row.MaildirFile);
            bool removed = await owner.store.DeleteMessageAsync(id, ct).ConfigureAwait(false);
            if (removed)
            {
                await owner.store.AddMailboxUsageAsync(mailbox.Id, -row.SizeBytes, ct).ConfigureAwait(false);
            }
            return removed;
        }

        public async Task<(Guid? Id, string? Refusal)> AppendAsync(string folder, byte[] raw, bool seen, bool flagged, bool answered, DateTimeOffset? arrived, CancellationToken ct = default)
        {
            using IDisposable keys = MailKeyring.Use(ring);
            if (await this.FolderAsync(folder, ct).ConfigureAwait(false) is not FolderRow target)
            {
                return (null, "[TRYCREATE] There is no folder by that name.");
            }
            if ((await StoragePlan.StateOfAsync(owner.store, mailbox, ct).ConfigureAwait(false)).Full)
            {
                return (null, "[OVERQUOTA] Your mailbox is full.");
            }
            Anjal.Mime.MimeMessage? parsed = Parse(raw);
            if (AttachmentPolicy.RefusedInside(parsed) is string refused)
            {
                return (null, "Not kept: " + refused + ".");
            }
            string messageId = parsed?.MessageId ?? string.Empty;
            // A mail app files its own copy of what it sent; Anjal already filed one (DEF-002) - keep one.
            if (messageId.Length > 0 && string.Equals(target.Name, "Sent", StringComparison.OrdinalIgnoreCase))
            {
                IReadOnlyList<MessageRow> recent = await owner.store.ListMessagesAsync(mailbox.Id, target.Id, 200, 0, ct).ConfigureAwait(false);
                if (recent.FirstOrDefault(m => string.Equals(m.MessageId, messageId, StringComparison.Ordinal)) is MessageRow already)
                {
                    return (already.Id, null);
                }
            }
            MaildirWriteResult written = await owner.maildir.WriteAsync(tenant.Slug, mailbox.Address, target.Name, raw, ct).ConfigureAwait(false);
            string? flaggedPath = owner.maildir.SetFlags(tenant.Slug, mailbox.Address, target.Name, written.RelativePath, seen, flagged, answered);
            MessageRow saved = await owner.store.SaveMessageAsync(new MessageRow
            {
                MailboxId = mailbox.Id,
                FolderId = target.Id,
                MaildirFile = flaggedPath ?? written.RelativePath,
                EnvelopeFrom = string.Empty,
                MessageId = messageId,
                FromHeader = parsed?.Headers.Get("From") ?? string.Empty,
                ToHeader = parsed?.Headers.Get("To") ?? string.Empty,
                Subject = parsed?.Subject ?? string.Empty,
                DateHeader = parsed?.Date ?? string.Empty,
                SizeBytes = written.SizeBytes,
                Seen = seen,
                Flagged = flagged,
                Answered = answered,
                SpamChecked = false,
                HasAttachments = MailboxSink.HasAttachment(parsed?.Body),
                BodyText = MessageText.Extract(parsed),
            }, ct).ConfigureAwait(false);
            if (arrived is DateTimeOffset at)
            {
                await owner.apps.SetReceivedAtAsync(saved.Id, at, ct).ConfigureAwait(false);
            }
            await owner.store.AddMailboxUsageAsync(mailbox.Id, written.SizeBytes, ct).ConfigureAwait(false);
            return (saved.Id, null);
        }

        public async Task<string?> CreateAsync(string name, CancellationToken ct = default)
        {
            using IDisposable keys = MailKeyring.Use(ring);
            string n = name.Trim();
            if (n.Length == 0 || n.Length > 100 || !FolderRow.IsValidName(n))
            {
                return "Folder names are 1 to 100 characters, without / or \\.";
            }
            if ((await owner.store.ListFoldersAsync(mailbox.Id, ct).ConfigureAwait(false)).Any(f => string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)))
            {
                return "[ALREADYEXISTS] There is already a folder with this name.";
            }
            await owner.store.EnsureFolderAsync(mailbox.Id, n, ct).ConfigureAwait(false);
            return null;
        }

        public async Task<string?> RemoveAsync(string name, CancellationToken ct = default)
        {
            using IDisposable keys = MailKeyring.Use(ring);
            if (await this.FolderAsync(name, ct).ConfigureAwait(false) is not FolderRow folder)
            {
                return "[NONEXISTENT] There is no folder by that name.";
            }
            if (SpecialUse.ContainsKey(folder.Name) || folder.Name == FolderRow.Inbox)
            {
                return "[CANNOT] A standard folder stays.";
            }
            return await owner.store.DeleteEmptyFolderAsync(mailbox.Id, folder.Id, ct).ConfigureAwait(false) ? null : "[INUSE] Only an empty folder can be removed.";
        }

        public Task<string?> RenameAsync(string oldName, string newName, CancellationToken ct = default) =>
            Task.FromResult<string?>("[CANNOT] Folders are renamed in Anjal's webmail.");

        private async Task<FolderRow?> FolderAsync(string name, CancellationToken ct)
        {
            string n = string.Equals(name, FolderRow.Inbox, StringComparison.OrdinalIgnoreCase) ? FolderRow.Inbox : name;
            if (Hidden.Contains(n))
            {
                return null;
            }
            // The standard folders are always there for an app, made on first use as the webmail makes them.
            if (n == FolderRow.Inbox || SpecialUse.ContainsKey(n))
            {
                return await owner.store.EnsureFolderAsync(mailbox.Id, SpecialUse.Keys.FirstOrDefault(k => string.Equals(k, n, StringComparison.OrdinalIgnoreCase)) ?? n, ct).ConfigureAwait(false);
            }
            return (await owner.store.ListFoldersAsync(mailbox.Id, ct).ConfigureAwait(false)).FirstOrDefault(f => string.Equals(f.Name, n, StringComparison.Ordinal))
                ?? (await owner.store.ListFoldersAsync(mailbox.Id, ct).ConfigureAwait(false)).FirstOrDefault(f => string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase));
        }

        private async Task<(MessageRow Row, FolderRow Folder)?> OwnedAsync(Guid id, CancellationToken ct)
        {
            if (await owner.store.GetMessageByIdAsync(id, ct).ConfigureAwait(false) is not MessageRow row || row.MailboxId != mailbox.Id)
            {
                return null;
            }
            FolderRow? folder = (await owner.store.ListFoldersAsync(mailbox.Id, ct).ConfigureAwait(false)).FirstOrDefault(f => f.Id == row.FolderId);
            return folder is null ? null : (row, folder);
        }

        private static Anjal.Mime.MimeMessage? Parse(byte[] raw)
        {
            try
            {
                return Anjal.Mime.MimeParser.Parse(raw);
            }
#pragma warning disable CA1031 // An unreadable message is still kept; its bytes are the truth.
            catch (Exception)
            {
                return null;
            }
#pragma warning restore CA1031
        }
    }
}
