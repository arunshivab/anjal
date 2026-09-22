using System.Globalization;
using System.Text;
using Anjal.Mailbox;
using Anjal.Mime;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>A folder with its message count, for the sidebar.</summary>
public sealed class FolderView
{
    /// <summary>Folder id.</summary>
    public Guid Id { get; init; }

    /// <summary>Folder name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Messages in the folder.</summary>
    public long Count { get; init; }

    /// <summary>Unread messages in the folder.</summary>
    public long Unread { get; init; }
}

/// <summary>One attachment of an opened message.</summary>
public sealed class AttachmentView
{
    /// <summary>Zero-based index among the message's attachments.</summary>
    public int Index { get; init; }

    /// <summary>File name from Content-Disposition or Content-Type, or a generated name.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>MIME type.</summary>
    public string ContentType { get; init; } = "application/octet-stream";

    /// <summary>Decoded size in bytes.</summary>
    public long SizeBytes { get; init; }
}

/// <summary>An opened message, ready to render.</summary>
public sealed class MessageView
{
    /// <summary>The index row.</summary>
    public MessageRow Row { get; init; } = new();

    /// <summary>Folder the message is in.</summary>
    public FolderRow Folder { get; init; } = new();

    /// <summary>Decoded From header.</summary>
    public string From { get; init; } = string.Empty;

    /// <summary>Decoded To header.</summary>
    public string To { get; init; } = string.Empty;

    /// <summary>Decoded Cc header.</summary>
    public string Cc { get; init; } = string.Empty;

    /// <summary>Decoded Subject.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>Raw Date header.</summary>
    public string Date { get; init; } = string.Empty;

    /// <summary>Sanitised HTML to place in the sandboxed iframe.</summary>
    public string BodyHtml { get; init; } = string.Empty;

    /// <summary>Whether the body came from a text/html part (as opposed to plain text wrapped as HTML).</summary>
    public bool IsHtml { get; init; }

    /// <summary>Whether remote images were blocked during sanitising.</summary>
    public bool HasBlockedImages { get; init; }

    /// <summary>The <c>X-Anjal-Spam-Reasons</c> header, or empty.</summary>
    public string SpamReasons { get; init; } = string.Empty;

    /// <summary>Attachments in order.</summary>
    public IReadOnlyList<AttachmentView> Attachments { get; init; } = Array.Empty<AttachmentView>();
}

/// <summary>Input for composing a new message.</summary>
public sealed class ComposeRequest
{
    /// <summary>To header value (one or more addresses).</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>Cc header value.</summary>
    public string Cc { get; set; } = string.Empty;

    /// <summary>Bcc header value. Recipients receive the message but the header is not sent.</summary>
    public string Bcc { get; set; } = string.Empty;

    /// <summary>
    /// The draft this compose is editing, if any. On send or save the old
    /// draft is replaced, so a draft never duplicates itself.
    /// </summary>
    public Guid? DraftId { get; set; }

    /// <summary>Message-ID of the message being replied to, for In-Reply-To and References.</summary>
    public string InReplyTo { get; set; } = string.Empty;

    /// <summary>Subject.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Plain-text body.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Attachments as (file name, content type, bytes).</summary>
    public IList<(string FileName, string ContentType, byte[] Bytes)> Attachments { get; } = new List<(string, string, byte[])>();

    /// <summary>
    /// A message whose attachments this one carries: the original of a
    /// forward, or the draft being edited. Its files are copied by index
    /// (<see cref="CarryIndexes"/>), so nothing is lost silently (DEF-006, DEF-029).
    /// </summary>
    public Guid? CarryFrom { get; set; }

    /// <summary>
    /// The formatted body from the editor, when JavaScript is on. Sanitised
    /// before use; when present the message is sent as HTML with a plain-text
    /// version derived from it. Empty means plain text only.
    /// </summary>
    public string BodyHtml { get; set; } = string.Empty;

    /// <summary>Which of <see cref="CarryFrom"/>'s attachments to include, by index.</summary>
    public IList<int> CarryIndexes { get; } = new List<int>();
}

/// <summary>
/// Mailbox operations behind the webmail pages. Every method takes the
/// signed-in mailbox id and refuses to touch messages that belong to a
/// different mailbox, so the pages never need to check ownership.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>Folders every mailbox shows even before any mail arrives.</summary>
    public static readonly IReadOnlyList<string> DefaultFolders = new[] { FolderRow.Inbox, "Sent", "Drafts", MailboxSink.JunkFolder, "Trash" };

    private readonly IMailboxStore store;
    private readonly IMessageStore messageStore;
    private readonly IMaildirStore maildir;
    private readonly string hostName;

    /// <summary>Construct.</summary>
    /// <param name="store">Mailbox registry and message index.</param>
    /// <param name="messageStore">Outbound queue.</param>
    /// <param name="maildir">Filesystem store for message bodies.</param>
    /// <param name="hostName">Host name used in generated Message-IDs.</param>
    public MailboxService(IMailboxStore store, IMessageStore messageStore, IMaildirStore maildir, string hostName)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(messageStore);
        ArgumentNullException.ThrowIfNull(maildir);
        ArgumentNullException.ThrowIfNull(hostName);
        this.store = store;
        this.messageStore = messageStore;
        this.maildir = maildir;
        this.hostName = hostName;
    }

    /// <summary>Load the signed-in mailbox and its tenant. Null if either is missing or disabled.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<(TenantRow Tenant, MailboxRow Mailbox)?> GetContextAsync(Guid mailboxId, CancellationToken ct = default)
    {
        MailboxRow? mailbox = await this.store.GetMailboxByIdAsync(mailboxId, ct).ConfigureAwait(false);
        if (mailbox is null || !mailbox.Enabled)
        {
            return null;
        }
        TenantRow? tenant = await this.store.GetTenantByIdAsync(mailbox.TenantId, ct).ConfigureAwait(false);
        if (tenant is null || !tenant.Enabled)
        {
            return null;
        }
        return (tenant, mailbox);
    }

    /// <summary>List folders with counts, INBOX first. Ensures the default folders exist.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyList<FolderView>> ListFoldersAsync(Guid mailboxId, CancellationToken ct = default)
    {
        foreach (string name in DefaultFolders)
        {
            await this.store.EnsureFolderAsync(mailboxId, name, ct).ConfigureAwait(false);
        }
        var result = new List<FolderView>();
        foreach (FolderRow f in await this.store.ListFoldersAsync(mailboxId, ct).ConfigureAwait(false))
        {
            long count = await this.store.CountMessagesAsync(mailboxId, f.Id, ct).ConfigureAwait(false);
            long unread = await this.store.CountUnreadAsync(mailboxId, f.Id, ct).ConfigureAwait(false);
            result.Add(new FolderView { Id = f.Id, Name = f.Name, Count = count, Unread = unread });
        }
        return result;
    }

    /// <summary>Find a folder of the mailbox by name (case-sensitive). Null if none.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="name">Folder name.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<FolderRow?> GetFolderAsync(Guid mailboxId, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (FolderRow f in await this.store.ListFoldersAsync(mailboxId, ct).ConfigureAwait(false))
        {
            if (string.Equals(f.Name, name, StringComparison.Ordinal))
            {
                return f;
            }
        }
        return null;
    }

    /// <summary>Page a folder's messages, newest first.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="folderId">The folder.</param>
    /// <param name="page">Zero-based page.</param>
    /// <param name="pageSize">Rows per page.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<(IReadOnlyList<MessageRow> Items, long Total)> ListMessagesAsync(Guid mailboxId, Guid folderId, int page, int pageSize, CancellationToken ct = default)
    {
        int size = Math.Clamp(pageSize, 1, 200);
        int offset = Math.Max(0, page) * size;
        IReadOnlyList<MessageRow> items = await this.store.ListMessagesAsync(mailboxId, folderId, size, offset, ct).ConfigureAwait(false);
        long total = await this.store.CountMessagesAsync(mailboxId, folderId, ct).ConfigureAwait(false);
        return (items, total);
    }

    /// <summary>
    /// Fetch a message row, verifying it belongs to the mailbox. Null if
    /// missing or owned by another mailbox.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The message.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<MessageRow?> GetOwnedRowAsync(Guid mailboxId, Guid messageId, CancellationToken ct = default)
    {
        MessageRow? row = await this.store.GetMessageByIdAsync(messageId, ct).ConfigureAwait(false);
        return row is not null && row.MailboxId == mailboxId ? row : null;
    }

    /// <summary>Read and parse the raw bytes of an owned message. Null if not found on disk.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The message.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<(MessageRow Row, FolderRow Folder, byte[] Raw)?> ReadRawAsync(Guid mailboxId, Guid messageId, CancellationToken ct = default)
    {
        (TenantRow tenant, MailboxRow mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        MessageRow? row = await this.GetOwnedRowAsync(mailboxId, messageId, ct).ConfigureAwait(false);
        if (context is null || row is null)
        {
            return null;
        }
        FolderRow? folder = await this.FolderByIdAsync(mailboxId, row.FolderId, ct).ConfigureAwait(false);
        if (folder is null)
        {
            return null;
        }
        byte[]? raw = await this.maildir.ReadAsync(context.Value.tenant.Slug, context.Value.mailbox.Address, folder.Name, row.MaildirFile, ct).ConfigureAwait(false);
        return raw is null ? null : (row, folder, raw);
    }

    /// <summary>
    /// Open a message for display: parses MIME, picks the best body part,
    /// sanitises HTML, lists attachments, and marks the message seen.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The message.</param>
    /// <param name="allowRemoteImages">Whether to let remote images load.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<MessageView?> OpenAsync(Guid mailboxId, Guid messageId, bool allowRemoteImages, CancellationToken ct = default)
    {
        (MessageRow Row, FolderRow Folder, byte[] Raw)? loaded = await this.ReadRawAsync(mailboxId, messageId, ct).ConfigureAwait(false);
        if (loaded is null)
        {
            return null;
        }
        (MessageRow row, FolderRow folder, byte[] raw) = loaded.Value;

        MimeMessage? parsed = TryParse(raw);
        MimePart? htmlPart = null;
        MimePart? textPart = null;
        var attachments = new List<(MimePart Part, AttachmentView View)>();
        if (parsed is not null)
        {
            Walk(parsed.Body, ref htmlPart, ref textPart, attachments);
        }

        string bodyHtml;
        bool isHtml;
        bool blocked = false;
        if (htmlPart is not null)
        {
            string html = htmlPart.GetBodyAsText();
            bodyHtml = HtmlSanitizer.Sanitize(html, allowRemoteImages);
            isHtml = true;
            blocked = !allowRemoteImages && bodyHtml.Contains("data-blocked-src=", StringComparison.Ordinal);
        }
        else if (textPart is not null)
        {
            bodyHtml = HtmlSanitizer.FromPlainText(textPart.GetBodyAsText());
            isHtml = false;
        }
        else
        {
            bodyHtml = HtmlSanitizer.FromPlainText(Encoding.UTF8.GetString(raw));
            isHtml = false;
        }

        if (!row.Seen)
        {
            row = await this.SetFlagsAsync(mailboxId, messageId, seen: true, row.Flagged, row.Answered, ct).ConfigureAwait(false) ?? row;
        }

        var views = new List<AttachmentView>(attachments.Count);
        foreach ((MimePart _, AttachmentView v) in attachments)
        {
            views.Add(v);
        }

        return new MessageView
        {
            Row = row,
            Folder = folder,
            From = DecodeHeader(parsed, "From", row.FromHeader),
            To = DecodeHeader(parsed, "To", row.ToHeader),
            Cc = DecodeHeader(parsed, "Cc", string.Empty),
            Subject = parsed?.Subject ?? row.Subject,
            Date = parsed?.Date ?? row.DateHeader,
            BodyHtml = bodyHtml,
            IsHtml = isHtml,
            HasBlockedImages = blocked,
            SpamReasons = parsed?.Headers.Get(Anjal.Spam.SpamHeaders.Reasons) ?? string.Empty,
            Attachments = views,
        };
    }

    /// <summary>Fetch one attachment's bytes. Null if the message or index is unknown.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The message.</param>
    /// <param name="index">Attachment index from <see cref="AttachmentView.Index"/>.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<(AttachmentView View, byte[] Bytes)?> GetAttachmentAsync(Guid mailboxId, Guid messageId, int index, CancellationToken ct = default)
    {
        (MessageRow Row, FolderRow Folder, byte[] Raw)? loaded = await this.ReadRawAsync(mailboxId, messageId, ct).ConfigureAwait(false);
        if (loaded is null)
        {
            return null;
        }
        MimeMessage? parsed = TryParse(loaded.Value.Raw);
        if (parsed is null)
        {
            return null;
        }
        MimePart? html = null;
        MimePart? text = null;
        var attachments = new List<(MimePart Part, AttachmentView View)>();
        Walk(parsed.Body, ref html, ref text, attachments);
        if (index < 0 || index >= attachments.Count)
        {
            return null;
        }
        return (attachments[index].View, attachments[index].Part.Body);
    }

    /// <summary>Set flags on an owned message, renaming the Maildir file to match.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The message.</param>
    /// <param name="seen">Seen flag.</param>
    /// <param name="flagged">Flagged flag.</param>
    /// <param name="answered">Answered flag.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<MessageRow?> SetFlagsAsync(Guid mailboxId, Guid messageId, bool seen, bool flagged, bool answered, CancellationToken ct = default)
    {
        (TenantRow tenant, MailboxRow mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        MessageRow? row = await this.GetOwnedRowAsync(mailboxId, messageId, ct).ConfigureAwait(false);
        if (context is null || row is null)
        {
            return null;
        }
        FolderRow? folder = await this.FolderByIdAsync(mailboxId, row.FolderId, ct).ConfigureAwait(false);
        string? renamed = folder is null ? null
            : this.maildir.SetFlags(context.Value.tenant.Slug, context.Value.mailbox.Address, folder.Name, row.MaildirFile, seen, flagged, answered);
        return await this.store.SetMessageFlagsAsync(messageId, seen, flagged, answered, renamed, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Move an owned message to another folder of the same mailbox
    /// (creating the folder if needed). Null if the message is unknown.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The message.</param>
    /// <param name="toFolderName">Destination folder name.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<MessageRow?> MoveAsync(Guid mailboxId, Guid messageId, string toFolderName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(toFolderName);
        (TenantRow tenant, MailboxRow mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        MessageRow? row = await this.GetOwnedRowAsync(mailboxId, messageId, ct).ConfigureAwait(false);
        if (context is null || row is null)
        {
            return null;
        }
        FolderRow? from = await this.FolderByIdAsync(mailboxId, row.FolderId, ct).ConfigureAwait(false);
        if (from is null)
        {
            return null;
        }
        // A move may only target a folder that already exists in this
        // mailbox. The name arrives from a form field; creating folders from
        // it would let a request invent arbitrary rows and directories (and
        // "../x" would reach the filesystem layer before being refused).
        // The system folders are always valid targets and are created on
        // first use; any other name must already exist.
        FolderRow? to = DefaultFolders.Contains(toFolderName)
            ? await this.store.EnsureFolderAsync(mailboxId, toFolderName, ct).ConfigureAwait(false)
            : await this.GetFolderAsync(mailboxId, toFolderName, ct).ConfigureAwait(false);
        if (to is null)
        {
            return null;
        }
        if (to.Id == from.Id)
        {
            return row;
        }
        string? moved = this.maildir.Move(context.Value.tenant.Slug, context.Value.mailbox.Address, from.Name, row.MaildirFile, to.Name);
        return await this.store.MoveMessageAsync(messageId, to.Id, moved ?? row.MaildirFile, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// "Not spam": move the message to INBOX and add an allow rule for its
    /// sender so future mail from that address goes straight to INBOX.
    /// Returns the moved row, or null if the message is unknown.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The message.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<MessageRow?> MarkNotSpamAsync(Guid mailboxId, Guid messageId, CancellationToken ct = default)
    {
        MessageRow? moved = await this.MoveAsync(mailboxId, messageId, FolderRow.Inbox, ct).ConfigureAwait(false);
        if (moved is not null)
        {
            await this.AddSenderRuleAsync(mailboxId, moved, SenderRuleAction.Allow, ct).ConfigureAwait(false);
        }
        return moved;
    }

    /// <summary>
    /// "Report spam": move the message to Junk and add a block rule for
    /// its sender. Returns the moved row, or null if the message is unknown.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The message.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<MessageRow?> ReportSpamAsync(Guid mailboxId, Guid messageId, CancellationToken ct = default)
    {
        MessageRow? moved = await this.MoveAsync(mailboxId, messageId, MailboxSink.JunkFolder, ct).ConfigureAwait(false);
        if (moved is not null)
        {
            await this.AddSenderRuleAsync(mailboxId, moved, SenderRuleAction.Block, ct).ConfigureAwait(false);
        }
        return moved;
    }

    /// <summary>
    /// The sender address a rule should target: the From header address if
    /// present, else the envelope sender. Empty if neither is usable.
    /// </summary>
    /// <param name="row">The message row.</param>
    public static string SenderOf(MessageRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        string from = Anjal.Spam.SpamScorer.FirstAddress(Anjal.Mime.EncodedWordDecoder.Decode(row.FromHeader));
        return from.Length > 0 ? from : row.EnvelopeFrom.Trim().ToLowerInvariant();
    }

    private async Task AddSenderRuleAsync(Guid mailboxId, MessageRow row, SenderRuleAction action, CancellationToken ct)
    {
        (TenantRow tenant, MailboxRow mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        string sender = SenderOf(row);
        if (context is null || sender.Length == 0 || !sender.Contains('@', StringComparison.Ordinal))
        {
            return;
        }
        // Personal to this mailbox. It used to be written tenant-wide, so one
        // person's Not spam let a sender past the filter for everyone (DEF-028).
        await this.store.UpsertMailboxSenderRuleAsync(mailboxId, sender, action, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Permanently delete an owned message: the Maildir file, the index
    /// row, and its bytes from the mailbox usage counter.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The message.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<bool> DeleteAsync(Guid mailboxId, Guid messageId, CancellationToken ct = default)
    {
        (TenantRow tenant, MailboxRow mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        MessageRow? row = await this.GetOwnedRowAsync(mailboxId, messageId, ct).ConfigureAwait(false);
        if (context is null || row is null)
        {
            return false;
        }
        FolderRow? folder = await this.FolderByIdAsync(mailboxId, row.FolderId, ct).ConfigureAwait(false);
        if (folder is not null)
        {
            this.maildir.Delete(context.Value.tenant.Slug, context.Value.mailbox.Address, folder.Name, row.MaildirFile);
        }
        bool removed = await this.store.DeleteMessageAsync(messageId, ct).ConfigureAwait(false);
        if (removed)
        {
            await this.store.AddMailboxUsageAsync(mailboxId, -row.SizeBytes, ct).ConfigureAwait(false);
        }
        return removed;
    }

    /// <summary>
    /// Build an RFC 5322 message from a compose form, enqueue it for each
    /// recipient, and file a copy in the Sent folder. Returns the error
    /// text to show the user, or <see langword="null"/> on success.
    /// </summary>
    /// <param name="mailboxId">The sending mailbox.</param>
    /// <param name="request">The compose form.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> SendAsync(Guid mailboxId, ComposeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        (TenantRow tenant, MailboxRow mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        if (context is null)
        {
            return "Mailbox is not available.";
        }
        (TenantRow tenant, MailboxRow mailbox) = context.Value;
        if (QuotaPolicy.IsFull(mailbox))
        {
            return "Your mailbox is full. Delete some messages (Trash, then Delete permanently) before sending.";
        }

        IReadOnlyList<MailAddress> to = AddressParser.Parse(request.To);
        IReadOnlyList<MailAddress> cc = AddressParser.Parse(request.Cc);
        IReadOnlyList<MailAddress> bcc = AddressParser.Parse(request.Bcc);
        if (to.Count == 0)
        {
            return "At least one valid To address is required.";
        }
        if (!string.IsNullOrWhiteSpace(request.Cc) && cc.Count == 0)
        {
            return "The Cc field contains no valid address.";
        }
        if (!string.IsNullOrWhiteSpace(request.Bcc) && bcc.Count == 0)
        {
            return "The Bcc field contains no valid address.";
        }
        if (string.IsNullOrWhiteSpace(request.Subject) && string.IsNullOrWhiteSpace(request.Body) && request.Attachments.Count == 0)
        {
            return "The message is empty.";
        }

        byte[] raw = BuildMessage(mailbox, request, to, cc);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var recipients = new List<MailAddress>(to);
        recipients.AddRange(cc);
        recipients.AddRange(bcc);
        foreach (MailAddress rcpt in recipients)
        {
            await this.messageStore.EnqueueOutboundAsync(new OutboundMessage
            {
                EnvelopeFrom = mailbox.Address,
                EnvelopeTo = rcpt.Address,
                RawBytes = raw,
                CreatedAt = now,
                NextAttemptAt = now,
                GiveUpAt = now.AddHours(24),
            }, ct).ConfigureAwait(false);
        }

        // Sent copy: written already-seen straight into cur/.
        FolderRow sent = await this.store.EnsureFolderAsync(mailbox.Id, "Sent", ct).ConfigureAwait(false);
        MaildirWriteResult written = await this.maildir.WriteAsync(tenant.Slug, mailbox.Address, sent.Name, raw, ct).ConfigureAwait(false);
        string? seenPath = this.maildir.SetFlags(tenant.Slug, mailbox.Address, sent.Name, written.RelativePath, seen: true, flagged: false, answered: false);
        await this.store.SaveMessageAsync(new MessageRow
        {
            MailboxId = mailbox.Id,
            FolderId = sent.Id,
            // Recorded so the list can mark it and the dashboard can count it (DEF-005, DEF-016).
            HasAttachments = request.Attachments.Count > 0,
            BodyText = Anjal.Mailbox.MessageText.Extract(TryParse(raw)),
            MaildirFile = seenPath ?? written.RelativePath,
            EnvelopeFrom = mailbox.Address,
            MessageId = MessageIdOf(raw),
            FromHeader = FormatFrom(mailbox),
            ToHeader = request.To.Trim(),
            Subject = request.Subject.Trim(),
            DateHeader = FormatDate(now),
            SizeBytes = written.SizeBytes,
            Seen = true,
        }, ct).ConfigureAwait(false);
        await this.store.AddMailboxUsageAsync(mailbox.Id, written.SizeBytes, ct).ConfigureAwait(false);

        // The draft this was composed from is now redundant.
        if (request.DraftId is Guid draftId)
        {
            await this.DeleteAsync(mailbox.Id, draftId, ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>
    /// Build the wire bytes for a compose request. Public so tests can
    /// inspect the MIME structure without a store.
    /// </summary>
    /// <param name="mailbox">The sending mailbox.</param>
    /// <param name="request">The compose form.</param>
    /// <param name="to">Parsed To addresses.</param>
    /// <param name="cc">Parsed Cc addresses.</param>
    public static byte[] BuildMessage(MailboxRow mailbox, ComposeRequest request, IReadOnlyList<MailAddress> to, IReadOnlyList<MailAddress> cc)
        => BuildMessage(mailbox, request, to, cc, draftHeaders: null);

    /// <summary>
    /// Build the wire bytes, optionally as a draft. A draft keeps the
    /// half-typed To/Cc/Bcc text verbatim so reopening it shows exactly
    /// what was typed; a sent message uses the parsed addresses and never
    /// carries a Bcc header.
    /// </summary>
    /// <param name="mailbox">The sending mailbox.</param>
    /// <param name="request">The compose form.</param>
    /// <param name="to">Parsed To addresses.</param>
    /// <param name="cc">Parsed Cc addresses.</param>
    /// <param name="draftHeaders">Non-null when saving a draft; the raw field text to preserve.</param>
    public static byte[] BuildMessage(MailboxRow mailbox, ComposeRequest request, IReadOnlyList<MailAddress> to, IReadOnlyList<MailAddress> cc, ComposeRequest? draftHeaders)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(cc);

        var textPart = new MimePart();
        textPart.Headers.Add("Content-Type", "text/plain; charset=utf-8");
        textPart.Headers.Add("Content-Transfer-Encoding", "quoted-printable");
        string body = request.Body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        if (!body.EndsWith("\r\n", StringComparison.Ordinal))
        {
            body += "\r\n";
        }
        // Formatted mail carries both forms: the plain text is derived from the
        // sanitised HTML, never taken on trust from the browser.
        string cleanHtml = request.BodyHtml.Trim().Length == 0 ? string.Empty : HtmlSanitizer.Sanitize(request.BodyHtml);
        if (cleanHtml.Length > 0)
        {
            body = HtmlText.ToPlain(cleanHtml).Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";
        }
        textPart.SetBodyAsText(body);

        MimeEntity content = textPart;
        if (cleanHtml.Length > 0)
        {
            var htmlPart = new MimePart();
            htmlPart.Headers.Add("Content-Type", "text/html; charset=utf-8");
            htmlPart.Headers.Add("Content-Transfer-Encoding", "quoted-printable");
            htmlPart.SetBodyAsText("<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>" + cleanHtml + "</body></html>\r\n");
            MimeMultipart alternative = MultipartFactory.Create("alternative");
            alternative.Parts.Add(textPart);
            alternative.Parts.Add(htmlPart);
            content = alternative;
        }

        MimeEntity root;
        if (request.Attachments.Count == 0)
        {
            root = content;
        }
        else
        {
            MimeMultipart mixed = MultipartFactory.Create("mixed");
            mixed.Parts.Add(content);
            foreach ((string fileName, string contentType, byte[] bytes) in request.Attachments)
            {
                string safeName = SafeFileName(fileName);
                string type = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
                var part = new MimePart();
                part.Headers.Add("Content-Type", $"{type}; name=\"{safeName}\"");
                part.Headers.Add("Content-Disposition", $"attachment; filename=\"{safeName}\"");
                part.Headers.Add("Content-Transfer-Encoding", "base64");
                part.Body = bytes;
                mixed.Parts.Add(part);
            }
            root = mixed;
        }

        var msg = new MimeMessage(root);
        msg.Headers.Add("From", FormatFrom(mailbox));
        if (draftHeaders is not null)
        {
            // Draft fields are kept as typed, but a line break inside one
            // would start a new header on the wire, so it becomes a space.
            msg.Headers.Add("To", MimeHeader.Neutralise(draftHeaders.To.Trim()));
            if (draftHeaders.Cc.Trim().Length > 0)
            {
                msg.Headers.Add("Cc", MimeHeader.Neutralise(draftHeaders.Cc.Trim()));
            }
            if (draftHeaders.Bcc.Trim().Length > 0)
            {
                msg.Headers.Add("Bcc", MimeHeader.Neutralise(draftHeaders.Bcc.Trim()));
            }
        }
        else
        {
            msg.Headers.Add("To", FormatAddresses(to));
            if (cc.Count > 0)
            {
                msg.Headers.Add("Cc", FormatAddresses(cc));
            }
        }
        // Reply linkage is emitted only when it is a well-formed message id.
        // It arrives from a hidden form field, so it is attacker-controlled;
        // anything else is dropped and the reply simply is not threaded.
        string inReplyTo = request.InReplyTo.Trim().Trim('<', '>');
        if (IsValidMessageId(inReplyTo))
        {
            msg.Headers.Add("In-Reply-To", "<" + inReplyTo + ">");
            msg.Headers.Add("References", "<" + inReplyTo + ">");
        }
        msg.Subject = EncodeHeaderText(request.Subject.Trim());
        msg.Date = FormatDate(DateTimeOffset.UtcNow);
        msg.Headers.Add("Message-ID", $"<{Guid.NewGuid():N}@{mailbox.Domain}>");
        msg.Headers.Add("MIME-Version", "1.0");
        return MimeBuilder.Build(msg);
    }

    /// <summary>
    /// Whether a string is an acceptable message id (without angle brackets):
    /// <c>left@right</c>, printable ASCII, no whitespace, brackets or a second
    /// <c>@</c>, at most 250 characters.
    /// </summary>
    /// <param name="id">The candidate id.</param>
    public static bool IsValidMessageId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.Length == 0 || id.Length > 250)
        {
            return false;
        }
        int at = -1;
        for (int i = 0; i < id.Length; i++)
        {
            char c = id[i];
            if (c <= ' ' || c > '~' || c == '<' || c == '>')
            {
                return false;
            }
            if (c == '@')
            {
                if (at >= 0)
                {
                    return false;
                }
                at = i;
            }
        }
        return at > 0 && at < id.Length - 1;
    }

    /// <summary>
    /// RFC 2047 "B" encode a header value when it contains non-ASCII
    /// characters; return it unchanged otherwise.
    /// </summary>
    /// <param name="text">Header text.</param>
    public static string EncodeHeaderText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (char c in text)
        {
            if (c > 0x7E || c < 0x20)
            {
                return "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "?=";
            }
        }
        return text;
    }

    private static string FormatFrom(MailboxRow mailbox) =>
        string.IsNullOrWhiteSpace(mailbox.DisplayName)
            ? mailbox.Address
            : $"{QuoteDisplayName(mailbox.DisplayName)} <{mailbox.Address}>";

    private static string FormatAddresses(IReadOnlyList<MailAddress> addresses)
    {
        var parts = new List<string>(addresses.Count);
        foreach (MailAddress a in addresses)
        {
            parts.Add(string.IsNullOrEmpty(a.DisplayName) ? a.Address : $"{QuoteDisplayName(a.DisplayName)} <{a.Address}>");
        }
        return string.Join(", ", parts);
    }

    private static string QuoteDisplayName(string name)
    {
        string encoded = EncodeHeaderText(name);
        if (!ReferenceEquals(encoded, name))
        {
            return encoded;
        }
        return "\"" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string FormatDate(DateTimeOffset when) =>
        when.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture);

    private static string SafeFileName(string fileName)
    {
        string name = Path.GetFileName(fileName ?? string.Empty);
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(c == '"' || c == '\\' || c < 0x20 || c > 0x7E ? '_' : c);
        }
        return sb.Length == 0 ? "attachment" : sb.ToString();
    }

    private static string MessageIdOf(byte[] raw)
    {
        MimeMessage? parsed = TryParse(raw);
        return parsed?.MessageId ?? string.Empty;
    }

    private static string DecodeHeader(MimeMessage? parsed, string name, string fallback)
    {
        string? raw = parsed?.Headers.Get(name);
        return raw is null ? EncodedWordDecoder.Decode(fallback) : EncodedWordDecoder.Decode(raw);
    }

    private static MimeMessage? TryParse(byte[] raw)
    {
        try
        {
            return MimeParser.Parse(raw);
        }
#pragma warning disable CA1031 // A malformed message is displayed as raw text rather than failing the page.
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    private async Task<FolderRow?> FolderByIdAsync(Guid mailboxId, Guid folderId, CancellationToken ct)
    {
        foreach (FolderRow f in await this.store.ListFoldersAsync(mailboxId, ct).ConfigureAwait(false))
        {
            if (f.Id == folderId)
            {
                return f;
            }
        }
        return null;
    }

    /// <summary>
    /// Walk a MIME tree choosing the best body parts and collecting
    /// attachments. In multipart/alternative the last text/html and
    /// text/plain siblings win; every other leaf that is not the chosen
    /// body is an attachment.
    /// </summary>
    private static void Walk(MimeEntity entity, ref MimePart? html, ref MimePart? text, List<(MimePart Part, AttachmentView View)> attachments)
    {
        if (entity is MimeMultipart multi)
        {
            foreach (MimeEntity child in multi.Parts)
            {
                Walk(child, ref html, ref text, attachments);
            }
            return;
        }
        if (entity is not MimePart part)
        {
            return;
        }

        ContentType type = part.ContentType;
        string disposition = part.Headers.Get("Content-Disposition") ?? string.Empty;
        bool isAttachment = disposition.StartsWith("attachment", StringComparison.OrdinalIgnoreCase);
        string mime = type.MimeType;

        if (!isAttachment && mime == "text/html" && html is null)
        {
            html = part;
            return;
        }
        if (!isAttachment && mime == "text/plain" && text is null)
        {
            text = part;
            return;
        }

        string fileName = FileNameOf(part, attachments.Count, type);
        attachments.Add((part, new AttachmentView
        {
            Index = attachments.Count,
            FileName = fileName,
            ContentType = mime,
            SizeBytes = part.Body.LongLength,
        }));
    }

    private static string FileNameOf(MimePart part, int index, ContentType type)
    {
        string disposition = part.Headers.Get("Content-Disposition") ?? string.Empty;
        string? name = ParameterOf(disposition, "filename");
        if (string.IsNullOrEmpty(name) && type.Parameters.TryGetValue("name", out string? n))
        {
            name = n;
        }
        if (string.IsNullOrEmpty(name))
        {
            name = $"attachment-{index + 1}";
        }
        return Path.GetFileName(EncodedWordDecoder.Decode(name));
    }

    private static string? ParameterOf(string headerValue, string parameter)
    {
        foreach (string piece in headerValue.Split(';'))
        {
            string trimmed = piece.Trim();
            int eq = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }
            if (string.Equals(trimmed.Substring(0, eq).Trim(), parameter, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed.Substring(eq + 1).Trim().Trim('"');
            }
        }
        return null;
    }
}
