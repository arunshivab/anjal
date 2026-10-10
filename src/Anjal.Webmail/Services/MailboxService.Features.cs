using System.Globalization;
using System.Text;
using Anjal.Mailbox;
using Anjal.Mime;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>One suggestion for the address fields.</summary>
public sealed class ContactSuggestion
{
    /// <summary>Display name, or empty.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The address.</summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>How many messages in Sent went to this address (higher sorts first).</summary>
    public int Count { get; init; }
}

/// <summary>A compose form prefilled from an existing message.</summary>
public sealed class ComposePrefill
{
    /// <summary>To field.</summary>
    public string To { get; init; } = string.Empty;

    /// <summary>Cc field.</summary>
    public string Cc { get; init; } = string.Empty;

    /// <summary>Subject with Re: or Fwd: as appropriate.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>Body: the quoted original beneath an attribution line, with a blank line above for the reply.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Message-ID of the original, for In-Reply-To.</summary>
    public string InReplyTo { get; init; } = string.Empty;
}

/// <summary>
/// The second half of <see cref="MailboxService"/>: drafts, search,
/// address suggestions, per-mailbox settings, bulk actions on a
/// selection, and reply/forward prefill. Every method takes the
/// signed-in mailbox id and refuses to touch another mailbox's data, the
/// same as the first half.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>Page size used by the folder and search views.</summary>
    public const int PageSize = 50;

    /// <summary>Themes a mailbox may choose: the house colour and each of the twenty named colours, light, dark or following the device (DES-11 D4).</summary>
    public static readonly IReadOnlyList<string> Themes = MailboxRow.ThemeColours.SelectMany(c => MailboxRow.ThemeModes.Select(m => c + "-" + m)).ToArray();

    /// <summary>Minimum length of a new password.</summary>
    /// <summary>Kept for callers; the rule itself lives in <see cref="Anjal.Smtp.PasswordPolicy"/>.</summary>
    public const int MinimumPasswordLength = Anjal.Smtp.PasswordPolicy.MinimumLength;

    // ---------------- Search ----------------

    /// <summary>
    /// Search one folder or the whole mailbox. Returns the page and the
    /// total number of matches.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="folderId">Folder to search, or null for all folders.</param>
    /// <param name="query">Case-insensitive substring; empty matches everything.</param>
    /// <param name="page">Zero-based page.</param>
    /// <param name="pageSize">Messages per page: the person's own choice (rc.11, item 14).</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<(IReadOnlyList<MessageRow> Items, long Total)> SearchAsync(Guid mailboxId, Guid? folderId, string query, int page, int pageSize = PageSize, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        int size = Math.Clamp(pageSize, 1, 200);
        int offset = Math.Max(0, page) * size;
        IReadOnlyList<MessageRow> items = await this.store.SearchMessagesAsync(mailboxId, folderId, query, size, offset, ct).ConfigureAwait(false);
        long total = await this.store.CountSearchAsync(mailboxId, folderId, query, ct).ConfigureAwait(false);
        return (items, total);
    }

    /// <summary>
    /// Search with the filters of UX-05: sender, a date range in the person's
    /// own zone, and "has an attachment". The words are matched as by
    /// <see cref="SearchAsync"/>; the filters narrow the result.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="folderId">Folder to search, or null for all folders.</param>
    /// <param name="query">Words; empty matches everything.</param>
    /// <param name="sender">Part of the sender's name or address; empty for anyone.</param>
    /// <param name="after">Received on or after this instant, or null.</param>
    /// <param name="before">Received before this instant, or null.</param>
    /// <param name="withAttachment">Only messages with an attachment.</param>
    /// <param name="page">Zero-based page.</param>
    /// <param name="pageSize">Messages per page.</param>
    /// <param name="score">Only checked messages with this spam score (10 is 10 and above), or null (rc.15: a dashboard bar opens its messages).</param>
    /// <param name="narrow">rc.15 (items 56, 58): a category, the messages rescued from Junk, or the biggest first; null for none.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<(IReadOnlyList<MessageRow> Items, long Total)> SearchFilteredAsync(Guid mailboxId, Guid? folderId, string query, string sender, DateTimeOffset? after, DateTimeOffset? before, bool withAttachment, int page, int pageSize = PageSize, int? score = null, SearchNarrowing? narrow = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(sender);
        SearchNarrowing n = narrow ?? SearchNarrowing.None;
        if (sender.Trim().Length == 0 && after is null && before is null && !withAttachment && score is null && n.IsNone)
        {
            return await this.SearchAsync(mailboxId, folderId, query, page, pageSize, ct).ConfigureAwait(false);
        }
        int size = Math.Clamp(pageSize, 1, 200);
        string who = sender.Trim();
        var matched = new List<MessageRow>();
        // The newest 5,000 word matches are filtered here: enough for a person's mailbox.
        for (int offset = 0; offset < 5000; offset += 500)
        {
            IReadOnlyList<MessageRow> chunk = await this.store.SearchMessagesAsync(mailboxId, folderId, query, 500, offset, ct).ConfigureAwait(false);
            matched.AddRange(chunk.Where(m =>
                (who.Length == 0 || EncodedWordDecoder.Decode(m.FromHeader).Contains(who, StringComparison.OrdinalIgnoreCase) || m.EnvelopeFrom.Contains(who, StringComparison.OrdinalIgnoreCase))
                && (after is null || m.ReceivedAt >= after.Value)
                && (before is null || m.ReceivedAt < before.Value)
                && (!withAttachment || m.HasAttachments)
                && (score is null || (m.SpamChecked == true && Math.Clamp(m.SpamScore, 0, MailFigures.TopScore) == score.Value))
                && (n.Category is null || m.CategoryId == n.Category)
                && (!n.Uncategorised || m.CategoryId is null)
                && (n.Only is null || n.Only.Contains(m.Id))));
            if (chunk.Count < 500)
            {
                break;
            }
        }
        if (n.BiggestFirst)
        {
            matched = matched.OrderByDescending(m => m.SizeBytes).ToList();
        }
        else
        {
            matched = Sorted(matched, n.Sort).ToList();
        }
        IReadOnlyList<MessageRow> items = matched.Skip(Math.Max(0, page) * size).Take(size).ToList();
        return (items, matched.Count);
    }

    /// <summary>Name the folder a message is in, for the search results' Folder column.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyDictionary<Guid, string>> FolderNamesAsync(Guid mailboxId, CancellationToken ct = default)
    {
        var map = new Dictionary<Guid, string>();
        foreach (FolderRow f in await this.store.ListFoldersAsync(mailboxId, ct).ConfigureAwait(false))
        {
            map[f.Id] = f.Name;
        }
        return map;
    }

    /// <summary>Unread messages in a folder of this mailbox.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="folderId">The folder.</param>
    /// <param name="ct">Cancellation.</param>
    public Task<long> UnreadInFolderAsync(Guid mailboxId, Guid folderId, CancellationToken ct = default) =>
        this.store.CountUnreadAsync(mailboxId, folderId, ct);

    // ---------------- Address suggestions ----------------

    /// <summary>
    /// Addresses this mailbox has written to, most used first, filtered by
    /// a prefix or substring. Read from the Sent folder's index rows, so
    /// there is no address book to maintain and nothing to keep in sync.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="query">What the user has typed so far; empty returns the most used.</param>
    /// <param name="limit">Maximum suggestions.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyList<ContactSuggestion>> SuggestContactsAsync(Guid mailboxId, string query, int limit = 8, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        FolderRow? sent = await this.GetFolderAsync(mailboxId, "Sent", ct).ConfigureAwait(false);

        // 500 most recent sent messages is plenty to learn who you write to.
        IReadOnlyList<MessageRow> rows = sent is null
            ? Array.Empty<MessageRow>()
            : await this.store.ListMessagesAsync(mailboxId, sent.Id, 500, 0, ct).ConfigureAwait(false);
        var byAddress = new Dictionary<string, (string Name, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (MessageRow row in rows)
        {
            foreach (MailAddress a in AddressParser.Parse(EncodedWordDecoder.Decode(row.ToHeader)))
            {
                string key = a.Address.ToLowerInvariant();
                byAddress.TryGetValue(key, out (string Name, int Count) existing);
                string existingName = existing.Name ?? string.Empty;
                string name = existingName.Length > 0 ? existingName : a.DisplayName ?? string.Empty;
                byAddress[key] = (name, existing.Count + 1);
            }
        }

        // rc.12: people who wrote to you are offered too, after those you wrote to.
        FolderRow? inbox = await this.GetFolderAsync(mailboxId, FolderRow.Inbox, ct).ConfigureAwait(false);
        if (inbox is not null)
        {
            foreach (MessageRow row in await this.store.ListMessagesAsync(mailboxId, inbox.Id, 300, 0, ct).ConfigureAwait(false))
            {
                foreach (MailAddress a in AddressParser.Parse(EncodedWordDecoder.Decode(row.FromHeader)))
                {
                    string key = a.Address.ToLowerInvariant();
                    if (byAddress.ContainsKey(key))
                    {
                        continue;
                    }
                    byAddress[key] = (a.DisplayName ?? string.Empty, 0);
                }
            }
        }

        // rc.12 (items 28-30): saved contacts come first and give the name;
        // then colleagues in the organisation; then the people written to.
        foreach ((string address, string name) in await this.ColleaguesAsync(mailboxId, ct).ConfigureAwait(false))
        {
            string key = address.ToLowerInvariant();
            byAddress.TryGetValue(key, out (string Name, int Count) existing);
            byAddress[key] = (name.Length > 0 ? name : existing.Name ?? string.Empty, existing.Count + 100);
        }
        foreach (Contact c in await this.ListContactsAsync(mailboxId, ct).ConfigureAwait(false))
        {
            string key = c.Address.ToLowerInvariant();
            byAddress.TryGetValue(key, out (string Name, int Count) existing);
            byAddress[key] = (c.DisplayName != c.Address ? c.DisplayName : existing.Name ?? string.Empty, existing.Count + 1000);
        }

        string q = query.Trim();
        var matches = new List<ContactSuggestion>();
        foreach (KeyValuePair<string, (string Name, int Count)> kv in byAddress)
        {
            if (q.Length == 0 ||
                kv.Key.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                kv.Value.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(new ContactSuggestion { Address = kv.Key, Name = kv.Value.Name, Count = kv.Value.Count });
            }
        }
        matches.Sort((a, b) =>
        {
            int c = b.Count.CompareTo(a.Count);
            return c != 0 ? c : string.CompareOrdinal(a.Address, b.Address);
        });
        return matches.Count <= limit ? matches : matches.GetRange(0, Math.Max(0, limit));
    }

    // ---------------- Drafts ----------------

    /// <summary>
    /// Save a compose form as a draft in the Drafts folder. Replaces the
    /// previous version when <see cref="ComposeRequest.DraftId"/> is set,
    /// so repeated autosaves leave exactly one draft. Returns the draft's
    /// message id.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="request">The compose form.</param>
    /// <param name="ct">Cancellation.</param>
    public Task<Guid?> SaveDraftAsync(Guid mailboxId, ComposeRequest request, CancellationToken ct = default) =>
        this.SaveComposeCopyAsync(mailboxId, request, "Drafts", DateTimeOffset.UtcNow, ct);

    /// <summary>
    /// Keep a compose form as a message in a folder: Drafts for a draft, or
    /// Scheduled for mail waiting for its send time (rc.12, items 8 and UX-02),
    /// whose Date line is then the time it will be sent. Replaces the version
    /// named by <see cref="ComposeRequest.DraftId"/>.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="request">The compose form.</param>
    /// <param name="folderName">Drafts or Scheduled.</param>
    /// <param name="dated">The Date line: now for a draft, the send time for scheduled mail.</param>
    /// <param name="ct">Cancellation.</param>
    internal async Task<Guid?> SaveComposeCopyAsync(Guid mailboxId, ComposeRequest request, string folderName, DateTimeOffset dated, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        if (context is null)
        {
            return null;
        }
        (TenantRow tenant, MailboxRow mailbox) = context.Value;

        // Addresses in a draft may be half-typed; keep the text as written.
        byte[] raw = BuildMessage(mailbox, request, AddressParser.Parse(request.To), AddressParser.Parse(request.Cc), draftHeaders: request);
        FolderRow drafts = await this.store.EnsureFolderAsync(mailbox.Id, folderName, ct).ConfigureAwait(false);
        MaildirWriteResult written = await this.maildir.WriteAsync(tenant.Slug, mailbox.Address, drafts.Name, raw, ct).ConfigureAwait(false);
        string? seenPath = this.maildir.SetFlags(tenant.Slug, mailbox.Address, drafts.Name, written.RelativePath, seen: true, flagged: false, answered: false);

        MessageRow saved = await this.store.SaveMessageAsync(new MessageRow
        {
            MailboxId = mailbox.Id,
            FolderId = drafts.Id,
            HasAttachments = request.Attachments.Count > 0,
            BodyText = Anjal.Mailbox.MessageText.Extract(TryParse(raw)),
            MaildirFile = seenPath ?? written.RelativePath,
            EnvelopeFrom = mailbox.Address,
            FromHeader = FormatFrom(mailbox),
            ToHeader = request.To.Trim(),
            Subject = request.Subject.Trim(),
            DateHeader = FormatDate(dated),
            SizeBytes = written.SizeBytes,
            Seen = true,
            SpamChecked = false,
        }, ct).ConfigureAwait(false);
        await this.store.AddMailboxUsageAsync(mailbox.Id, written.SizeBytes, ct).ConfigureAwait(false);

        // Remove the version this one replaces, after the new one is safely stored.
        if (request.DraftId is Guid previous && previous != saved.Id)
        {
            await this.DeleteAsync(mailboxId, previous, ct).ConfigureAwait(false);
        }
        return saved.Id;
    }

    /// <summary>
    /// Discard a draft for good: the "Discard" of "Keep this draft?" (rc.12,
    /// item 17). Only ever removes a message that is in Drafts, so a forged
    /// or mistaken request cannot delete any other mail. Returns true when
    /// a draft was removed.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="draftId">The draft.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<bool> DiscardDraftAsync(Guid mailboxId, Guid draftId, CancellationToken ct = default)
    {
        MessageRow? row = await this.GetOwnedRowAsync(mailboxId, draftId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }
        FolderRow? folder = await this.FolderByIdAsync(mailboxId, row.FolderId, ct).ConfigureAwait(false);
        if (folder is null || !string.Equals(folder.Name, "Drafts", StringComparison.Ordinal))
        {
            return false;
        }
        return await this.DeleteAsync(mailboxId, draftId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Load a draft back into a compose form. Returns null if the message
    /// is not a draft of this mailbox.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="draftId">The draft.</param>
    /// <param name="ct">Cancellation.</param>
    public Task<ComposeRequest?> LoadDraftAsync(Guid mailboxId, Guid draftId, CancellationToken ct = default) =>
        this.LoadComposeCopyAsync(mailboxId, draftId, "Drafts", ct);

    /// <summary>Load a kept compose form back from Drafts or Scheduled; null when it is not in that folder.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="draftId">The kept message.</param>
    /// <param name="folderName">The folder it must be in.</param>
    /// <param name="ct">Cancellation.</param>
    internal async Task<ComposeRequest?> LoadComposeCopyAsync(Guid mailboxId, Guid draftId, string folderName, CancellationToken ct = default)
    {
        (MessageRow Row, FolderRow Folder, byte[] Raw)? loaded = await this.ReadRawAsync(mailboxId, draftId, ct).ConfigureAwait(false);
        if (loaded is null || !string.Equals(loaded.Value.Folder.Name, folderName, StringComparison.Ordinal))
        {
            return null;
        }
        MimeMessage? parsed = TryParse(loaded.Value.Raw);
        if (parsed is null)
        {
            return null;
        }
        var request = new ComposeRequest
        {
            DraftId = draftId,
            To = EncodedWordDecoder.Decode(parsed.Headers.Get("To") ?? string.Empty),
            Cc = EncodedWordDecoder.Decode(parsed.Headers.Get("Cc") ?? string.Empty),
            Bcc = EncodedWordDecoder.Decode(parsed.Headers.Get("Bcc") ?? string.Empty),
            Subject = parsed.Subject ?? string.Empty,
            Body = FirstPlainText(parsed.Body),
            BodyHtml = FirstHtml(parsed.Body),
            InReplyTo = parsed.Headers.Get("In-Reply-To")?.Trim('<', '>', ' ') ?? string.Empty,
            Sender = EncodedWordDecoder.Decode(parsed.Headers.Get("Sender") ?? string.Empty),
        };
        return request;
    }

    // ---------------- Reply, reply all, forward ----------------

    /// <summary>How a compose form was opened from an existing message.</summary>
    public enum PrefillKind
    {
        /// <summary>Reply to the sender.</summary>
        Reply = 0,

        /// <summary>Reply to the sender and every other recipient.</summary>
        ReplyAll = 1,

        /// <summary>Forward to a new recipient.</summary>
        Forward = 2,
    }

    /// <summary>The longest signature accepted, in characters of HTML.</summary>
    public const int MaxSignatureChars = 10_000;

    /// <summary>This mailbox's signature, formatted and plain.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    public Task<(string Html, string Text)> GetSignatureAsync(Guid mailboxId, CancellationToken ct = default) =>
        this.store.GetSignatureAsync(mailboxId, ct);

    /// <summary>
    /// Save the signature. The HTML is sanitised and the plain form derived
    /// from it; with no HTML (JavaScript off) the plain text is used as typed.
    /// Returns a message for the user, or null on success.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="html">The editor's HTML, or empty.</param>
    /// <param name="text">The plain text box.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> SetSignatureAsync(Guid mailboxId, string html, string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(text);
        string clean = html.Trim().Length == 0 ? string.Empty : HtmlSanitizer.Sanitize(html).Trim();
        string plain = clean.Length > 0 ? HtmlText.ToPlain(clean) : text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (clean.Length == 0 && plain.Length > 0)
        {
            clean = HtmlText.FromQuotedPlain(plain);
        }
        if (clean.Length > MaxSignatureChars)
        {
            return "That signature is too long. Keep it to a few lines.";
        }
        await this.store.SetSignatureAsync(mailboxId, clean, plain, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// The opening body of a new message, reply or forward: space to write,
    /// then the signature (after the conventional "-- " line), then any
    /// quoted original - in plain and formatted forms that say the same.
    /// A reopened draft does not come through here, so its signature is
    /// never added twice.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="quoted">The quoted original (plain text, "&gt;" lines), or empty.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<(string Text, string Html)> StartBodyAsync(Guid mailboxId, string quoted, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(quoted);
        (string sigHtml, string sigText) = await this.store.GetSignatureAsync(mailboxId, ct).ConfigureAwait(false);
        string original = quoted.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('\n');
        var text = new System.Text.StringBuilder("\n\n");
        var html = new System.Text.StringBuilder("<div><br></div>");
        if (sigText.Length > 0)
        {
            text.Append("-- \n").Append(sigText).Append('\n');
            html.Append("<div data-signature=\"\">-- <br>").Append(sigHtml).Append("</div>");
        }
        if (original.Length > 0)
        {
            text.Append('\n').Append(original);
            html.Append("<div><br></div>").Append(HtmlText.FromQuotedPlain(original));
        }
        return (text.ToString(), html.ToString());
    }

    /// <summary>The first HTML body part, sanitised; empty when there is none.</summary>
    private static string FirstHtml(MimeEntity? body)
    {
        if (body is null)
        {
            return string.Empty;
        }
        MimePart? html = null;
        MimePart? text = null;
        var ignored = new List<(MimePart Part, AttachmentView View)>();
        Walk(body, ref html, ref text, ignored);
        return html is null ? string.Empty : HtmlSanitizer.Sanitize(html.GetBodyAsText());
    }

    /// <summary>The most attachment data one message may carry, before encoding: 18 MB.</summary>
    public const long MaxAttachmentBytes = 18L * 1024 * 1024;

    /// <summary>
    /// The attachments of one of this mailbox's messages, without marking it
    /// read. Null when the message is not this mailbox's.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The message.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyList<AttachmentView>?> ListAttachmentsAsync(Guid mailboxId, Guid messageId, CancellationToken ct = default)
    {
        (MessageRow Row, FolderRow Folder, byte[] Raw)? loaded = await this.ReadRawAsync(mailboxId, messageId, ct).ConfigureAwait(false);
        if (loaded is null)
        {
            return null;
        }
        MimeMessage? parsed = TryParse(loaded.Value.Raw);
        var found = new List<(MimePart Part, AttachmentView View)>();
        if (parsed is not null)
        {
            MimePart? html = null;
            MimePart? text = null;
            Walk(parsed.Body, ref html, ref text, found);
        }
        return found.ConvertAll(f => f.View);
    }

    /// <summary>
    /// Copy the chosen attachments of <see cref="ComposeRequest.CarryFrom"/>
    /// into the request, reading each through the same ownership check as a
    /// download. Returns a message for the user when the total, with any new
    /// uploads, would exceed <see cref="MaxAttachmentBytes"/>; otherwise null.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="request">The message being sent or saved.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> AddCarriedAttachmentsAsync(Guid mailboxId, ComposeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CarryFrom is Guid source)
        {
            foreach (int index in request.CarryIndexes.Distinct())
            {
                // rc.12 (item 23): -1 is the whole message, forwarded as an attachment.
                if (index == WholeMessage)
                {
                    if (await this.WholeMessageAttachmentAsync(mailboxId, source, ct).ConfigureAwait(false) is (AttachmentView whole, byte[] raw))
                    {
                        request.Attachments.Add((whole.FileName, whole.ContentType, raw));
                    }
                    continue;
                }
                (AttachmentView View, byte[] Bytes)? found = await this.GetAttachmentAsync(mailboxId, source, index, ct).ConfigureAwait(false);
                if (found is not null)
                {
                    request.Attachments.Add((found.Value.View.FileName, found.Value.View.ContentType, found.Value.Bytes));
                }
            }
        }
        long total = 0;
        foreach ((string _, string _, byte[] bytes) in request.Attachments)
        {
            total += bytes.LongLength;
        }
        return total > MaxAttachmentBytes
            ? "The attachments add up to more than 18 MB. Remove some, or send them in more than one message."
            : null;
    }

    /// <summary>
    /// The type to preview an attachment as (rc.12, UX-06): PNG, JPEG, GIF,
    /// WebP or PDF, judged by its declared type and its name; null when it
    /// is not shown in the page (SVG and HTML never are).
    /// </summary>
    /// <param name="a">The attachment.</param>
    public static string? PreviewType(AttachmentView a)
    {
        ArgumentNullException.ThrowIfNull(a);
        string ct = a.ContentType.ToLowerInvariant();
        string ext = Path.GetExtension(a.FileName).ToLowerInvariant();
        return (ct, ext) switch
        {
            (_, ".png") or ("image/png", _) => "image/png",
            (_, ".jpg" or ".jpeg") or ("image/jpeg", _) => "image/jpeg",
            (_, ".gif") or ("image/gif", _) => "image/gif",
            (_, ".webp") or ("image/webp", _) => "image/webp",
            (_, ".pdf") or ("application/pdf", _) => "application/pdf",
            _ => null,
        };
    }

    /// <summary>The carry index that means "the whole message, as an .eml attachment" (rc.12, item 23).</summary>
    public const int WholeMessage = -1;

    /// <summary>
    /// A whole message as an attachment: its original bytes, named after
    /// its subject. Sent as application/octet-stream: the attachment is base64,
    /// which RFC 2046 5.2.1 does not allow for message/rfc822, and every mail
    /// program opens an .eml file. Null when it is not this mailbox's.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The message.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<(AttachmentView View, byte[] Raw)?> WholeMessageAttachmentAsync(Guid mailboxId, Guid messageId, CancellationToken ct = default)
    {
        (MessageRow Row, FolderRow Folder, byte[] Raw)? loaded = await this.ReadRawAsync(mailboxId, messageId, ct).ConfigureAwait(false);
        if (loaded is null)
        {
            return null;
        }
        string subject = EncodedWordDecoder.Decode(loaded.Value.Row.Subject).Trim();
        string name = SafeFileName((subject.Length > 0 ? subject : "message") + ".eml");
        return (new AttachmentView { Index = WholeMessage, FileName = name, ContentType = "application/octet-stream", SizeBytes = loaded.Value.Raw.LongLength }, loaded.Value.Raw);
    }

    /// <summary>
    /// Build the prefilled compose form for a reply, reply-all or forward.
    /// Returns null when the message is not this mailbox's.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The message being answered.</param>
    /// <param name="kind">Reply, reply all or forward.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<ComposePrefill?> PrefillAsync(Guid mailboxId, Guid messageId, PrefillKind kind, CancellationToken ct = default)
    {
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        (MessageRow Row, FolderRow Folder, byte[] Raw)? loaded = await this.ReadRawAsync(mailboxId, messageId, ct).ConfigureAwait(false);
        if (context is null || loaded is null)
        {
            return null;
        }
        MimeMessage? parsed = TryParse(loaded.Value.Raw);
        string from = EncodedWordDecoder.Decode(parsed?.Headers.Get("From") ?? loaded.Value.Row.FromHeader);
        string to = EncodedWordDecoder.Decode(parsed?.Headers.Get("To") ?? loaded.Value.Row.ToHeader);
        string cc = EncodedWordDecoder.Decode(parsed?.Headers.Get("Cc") ?? string.Empty);
        string replyTo = EncodedWordDecoder.Decode(parsed?.Headers.Get("Reply-To") ?? string.Empty);
        string subject = parsed?.Subject ?? loaded.Value.Row.Subject;
        string date = parsed?.Date ?? loaded.Value.Row.DateHeader;
        string body = FirstPlainText(parsed?.Body);

        string toField;
        string ccField = string.Empty;
        if (kind == PrefillKind.Forward)
        {
            toField = string.Empty;
        }
        else
        {
            toField = replyTo.Length > 0 ? replyTo : from;
            if (kind == PrefillKind.ReplyAll)
            {
                // Everyone on To and Cc except this mailbox and the sender (already in To).
                var others = new List<string>();
                foreach (MailAddress a in AddressParser.Parse(to + (cc.Length > 0 ? ", " + cc : string.Empty)))
                {
                    if (!string.Equals(a.Address, context.Value.Mailbox.Address, StringComparison.OrdinalIgnoreCase) &&
                        !toField.Contains(a.Address, StringComparison.OrdinalIgnoreCase))
                    {
                        others.Add(a.DisplayName.Length > 0 ? $"{a.DisplayName} <{a.Address}>" : a.Address);
                    }
                }
                ccField = string.Join(", ", others);
            }
        }

        string prefix = kind == PrefillKind.Forward ? "Fwd: " : "Re: ";
        string newSubject = subject.StartsWith(prefix.TrimEnd(), StringComparison.OrdinalIgnoreCase)
            ? subject
            : prefix + subject;

        return new ComposePrefill
        {
            To = toField,
            Cc = ccField,
            Subject = newSubject,
            Body = QuoteForReply(from, date, body, kind == PrefillKind.Forward, to, cc, subject, ZonedClock.For(context.Value.Mailbox.TimeZone, context.Value.Mailbox.DateFormat)),
            InReplyTo = parsed?.MessageId ?? loaded.Value.Row.MessageId,
        };
    }

    /// <summary>
    /// Quote an original message as plain text: a blank line for the new
    /// text, an attribution line, then the original prefixed with "&gt; ".
    /// A forward uses the conventional header block instead of the
    /// attribution line.
    /// </summary>
    /// <param name="from">Original From.</param>
    /// <param name="date">Original Date header.</param>
    /// <param name="body">Original plain-text body.</param>
    /// <param name="forward">True for a forward.</param>
    /// <param name="to">Original To (forwards only).</param>
    /// <param name="cc">Original Cc (forwards only).</param>
    /// <param name="subject">Original subject (forwards only).</param>
    /// <param name="clock">The person's clock for the line above a reply; India time when absent.</param>
    public static string QuoteForReply(string from, string date, string body, bool forward, string to = "", string cc = "", string subject = "", ZonedClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(date);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(cc);
        ArgumentNullException.ThrowIfNull(subject);
        var sb = new StringBuilder("\n\n");
        if (forward)
        {
            sb.Append("---------- Forwarded message ----------\n");
            sb.Append("From: ").Append(from).Append('\n');
            if (date.Length > 0)
            {
                sb.Append("Date: ").Append(date).Append('\n');
            }
            if (subject.Length > 0)
            {
                sb.Append("Subject: ").Append(subject).Append('\n');
            }
            if (to.Length > 0)
            {
                sb.Append("To: ").Append(to).Append('\n');
            }
            if (cc.Length > 0)
            {
                sb.Append("Cc: ").Append(cc).Append('\n');
            }
            sb.Append('\n').Append(body.TrimEnd());
            return sb.ToString();
        }

        sb.Append(AttributionLine(from, date, clock)).Append('\n');
        foreach (string line in body.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd().Split('\n'))
        {
            sb.Append(line.Length == 0 ? ">" : "> " + line).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// The line above a quoted reply, in the replying person's time zone with
    /// the zone named, because the reply travels to people elsewhere (DEF-088):
    /// <c>On 20 September 2026 at 14:32 IST, Name wrote:</c>. Falls back to just
    /// the name when the date cannot be parsed.
    /// </summary>
    /// <param name="from">Original From header.</param>
    /// <param name="date">Original Date header.</param>
    /// <param name="clock">The person's clock; India time when absent.</param>
    public static string AttributionLine(string from, string date, ZonedClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(date);
        IReadOnlyList<MailAddress> parsed = AddressParser.Parse(from);
        string who = parsed.Count > 0 && parsed[0].DisplayName.Length > 0 ? parsed[0].DisplayName
            : parsed.Count > 0 ? parsed[0].Address
            : from;
        if (TryParseRfc5322(date, out DateTimeOffset when))
        {
            return $"On {(clock ?? ZonedClock.Default).Written(when)}, {who} wrote:";
        }
        return $"{who} wrote:";
    }

    /// <summary>
    /// Parse an RFC 5322 Date header such as
    /// <c>Sat, 20 Sep 2026 14:32:00 +0530</c>. .NET's general parser
    /// rejects the leading day-name, so it is removed first; a trailing
    /// zone name in parentheses is dropped too.
    /// </summary>
    /// <param name="date">The Date header value.</param>
    /// <param name="value">The parsed instant when the method returns true.</param>
    public static bool TryParseRfc5322(string date, out DateTimeOffset value)
    {
        ArgumentNullException.ThrowIfNull(date);
        string trimmed = date.Trim();

        int paren = trimmed.IndexOf('(', StringComparison.Ordinal);
        if (paren > 0)
        {
            trimmed = trimmed.Substring(0, paren).TrimEnd();
        }

        int comma = trimmed.IndexOf(',', StringComparison.Ordinal);
        if (comma > 0 && comma <= 4)
        {
            trimmed = trimmed.Substring(comma + 1).TrimStart();
        }

        return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    // ---------------- Bulk actions ----------------

    /// <summary>What a bulk action does to each selected message.</summary>
    public enum BulkAction
    {
        /// <summary>Move to Trash.</summary>
        Trash = 0,

        /// <summary>Mark seen.</summary>
        MarkRead = 1,

        /// <summary>Mark unseen.</summary>
        MarkUnread = 2,

        /// <summary>Move to Junk and block the sender.</summary>
        ReportSpam = 3,

        /// <summary>Move to INBOX and allow the sender.</summary>
        NotSpam = 4,

        /// <summary>Move from Trash back to INBOX.</summary>
        Restore = 5,

        /// <summary>Delete permanently. Only messages already in Trash are affected.</summary>
        Purge = 6,
    }

    /// <summary>
    /// Apply an action to a set of messages, skipping any that are not
    /// this mailbox's. Returns how many were changed.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageIds">Selected message ids.</param>
    /// <param name="action">What to do.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<int> BulkAsync(Guid mailboxId, IReadOnlyList<Guid> messageIds, BulkAction action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        int changed = 0;

        // Permanent deletion is only ever of mail already in Trash, so a
        // forged or mistaken request cannot skip the Trash step.
        FolderRow? trash = action == BulkAction.Purge
            ? await this.GetFolderAsync(mailboxId, "Trash", ct).ConfigureAwait(false)
            : null;
        // rc.14: nothing is deleted from a mailbox under a legal hold.
        if (action == BulkAction.Purge && await this.IsHeldAsync(mailboxId, ct).ConfigureAwait(false))
        {
            trash = null;
        }
        foreach (Guid id in messageIds)
        {
            MessageRow? row = await this.GetOwnedRowAsync(mailboxId, id, ct).ConfigureAwait(false);
            if (row is null)
            {
                continue;
            }
            if (action == BulkAction.Purge)
            {
                if (trash is not null && row.FolderId == trash.Id && await this.DeleteAsync(mailboxId, id, ct).ConfigureAwait(false))
                {
                    changed++;
                }
                continue;
            }
            object? result = action switch
            {
                BulkAction.Trash => await this.MoveAsync(mailboxId, id, "Trash", ct).ConfigureAwait(false),
                BulkAction.MarkRead => await this.SetFlagsAsync(mailboxId, id, true, row.Flagged, row.Answered, ct).ConfigureAwait(false),
                BulkAction.MarkUnread => await this.SetFlagsAsync(mailboxId, id, false, row.Flagged, row.Answered, ct).ConfigureAwait(false),
                BulkAction.ReportSpam => await this.ReportSpamAsync(mailboxId, id, ct).ConfigureAwait(false),
                BulkAction.Restore => await this.MoveAsync(mailboxId, id, FolderRow.Inbox, ct).ConfigureAwait(false),
                _ => await this.MarkNotSpamAsync(mailboxId, id, ct).ConfigureAwait(false),
            };
            if (result is not null)
            {
                changed++;
            }
        }
        return changed;
    }

    /// <summary>
    /// Move messages to a folder, noting where each was so the move can be
    /// undone for ten minutes (rc.11, UX-07: undo after delete or move).
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageIds">The messages.</param>
    /// <param name="toFolderName">Trash, INBOX, or a folder that exists.</param>
    /// <param name="now">The present moment.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many moved, and the token that undoes it (null when none moved).</returns>
    public async Task<(int Moved, Guid? UndoToken)> MoveWithUndoAsync(Guid mailboxId, IReadOnlyList<Guid> messageIds, string toFolderName, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        ArgumentNullException.ThrowIfNull(toFolderName);
        var moves = new List<(Guid MessageId, string FromFolder)>();
        foreach (Guid id in messageIds)
        {
            MessageRow? row = await this.GetOwnedRowAsync(mailboxId, id, ct).ConfigureAwait(false);
            FolderRow? from = row is null ? null : await this.FolderByIdAsync(mailboxId, row.FolderId, ct).ConfigureAwait(false);
            if (from is null || string.Equals(from.Name, toFolderName, StringComparison.Ordinal))
            {
                continue;
            }
            if (await this.MoveAsync(mailboxId, id, toFolderName, ct).ConfigureAwait(false) is not null)
            {
                moves.Add((id, from.Name));
            }
        }
        return moves.Count == 0 ? (0, null) : (moves.Count, this.Undo.Record(mailboxId, moves, now));
    }

    /// <summary>Put messages back where they were before a move (rc.11, UX-07).</summary>
    /// <param name="mailboxId">The mailbox asking.</param>
    /// <param name="token">The token from <see cref="MoveWithUndoAsync"/>.</param>
    /// <param name="now">The present moment.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many were put back; messages since deleted for good are skipped.</returns>
    public async Task<int> UndoMoveAsync(Guid mailboxId, Guid token, DateTimeOffset now, CancellationToken ct = default)
    {
        int restored = 0;
        foreach ((Guid messageId, string fromFolder) in this.Undo.Take(mailboxId, token, now))
        {
            if (await this.MoveAsync(mailboxId, messageId, fromFolder, ct).ConfigureAwait(false) is not null)
            {
                restored++;
            }
        }
        return restored;
    }

    /// <summary>
    /// Delete everything in Trash permanently. Returns how many were deleted.
    /// Works through the folder in pages; each pass re-reads the first page,
    /// and stops if a pass deletes nothing, so it cannot loop.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    public Task<int> EmptyTrashAsync(Guid mailboxId, CancellationToken ct = default) =>
        this.EmptyFolderAsync(mailboxId, "Trash", ct);

    /// <summary>
    /// Delete everything in Junk permanently (SPEC-11 item 25). Returns how
    /// many were deleted. As with Trash, nothing is deleted from a mailbox
    /// under a legal hold, and each message's evidence copy is kept for its
    /// own period.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many were deleted.</returns>
    public Task<int> EmptyJunkAsync(Guid mailboxId, CancellationToken ct = default) =>
        this.EmptyFolderAsync(mailboxId, Anjal.Mailbox.MailboxSink.JunkFolder, ct);

    private async Task<int> EmptyFolderAsync(Guid mailboxId, string name, CancellationToken ct)
    {
        FolderRow? trash = await this.GetFolderAsync(mailboxId, name, ct).ConfigureAwait(false);
        if (trash is null || await this.IsHeldAsync(mailboxId, ct).ConfigureAwait(false))
        {
            return 0;
        }
        int deleted = 0;
        while (true)
        {
            IReadOnlyList<MessageRow> batch = await this.store.ListMessagesAsync(mailboxId, trash.Id, 200, 0, ct).ConfigureAwait(false);
            int before = deleted;
            foreach (MessageRow m in batch)
            {
                if (await this.DeleteAsync(mailboxId, m.Id, ct).ConfigureAwait(false))
                {
                    deleted++;
                }
            }
            if (batch.Count == 0 || deleted == before)
            {
                return deleted;
            }
        }
    }

    /// <summary>Mark every message in a folder seen. Returns how many changed.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="folderId">The folder.</param>
    /// <param name="ct">Cancellation.</param>
    public Task<int> MarkFolderReadAsync(Guid mailboxId, Guid folderId, CancellationToken ct = default) =>
        this.MarkFolderAsync(mailboxId, folderId, seen: true, ct);

    /// <summary>Mark every message in a folder unread (rc.12, item 23). Returns how many changed.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="folderId">The folder.</param>
    /// <param name="ct">Cancellation.</param>
    public Task<int> MarkFolderUnreadAsync(Guid mailboxId, Guid folderId, CancellationToken ct = default) =>
        this.MarkFolderAsync(mailboxId, folderId, seen: false, ct);

    private async Task<int> MarkFolderAsync(Guid mailboxId, Guid folderId, bool seen, CancellationToken ct)
    {
        // One pass through the folder by offset. Marking a message seen does
        // not change its position, and the offset only ever grows, so this
        // ends even if a flag fails to persist - unlike re-reading "the first
        // unread page" until it comes back empty.
        const int batchSize = 200;
        int changed = 0;
        int offset = 0;
        while (true)
        {
            IReadOnlyList<MessageRow> batch = await this.store.ListMessagesAsync(mailboxId, folderId, batchSize, offset, ct).ConfigureAwait(false);
            foreach (MessageRow m in batch)
            {
                if (m.Seen != seen && await this.SetFlagsAsync(mailboxId, m.Id, seen, m.Flagged, m.Answered, ct).ConfigureAwait(false) is not null)
                {
                    changed++;
                }
            }
            if (batch.Count < batchSize)
            {
                return changed;
            }
            offset += batch.Count;
        }
    }

    // ---------------- Settings ----------------

    /// <summary>
    /// Change the display name used in the From header. Returns the error
    /// to show, or null on success.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="displayName">New display name (may be empty).</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> SetDisplayNameAsync(Guid mailboxId, string displayName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        if (displayName.Length > 100)
        {
            return "A display name can be at most 100 characters.";
        }
        foreach (char c in displayName)
        {
            if (char.IsControl(c))
            {
                return "A display name cannot contain line breaks or control characters.";
            }
        }
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        if (context is null)
        {
            return "Mailbox is not available.";
        }
        MailboxRow mailbox = context.Value.Mailbox;
        mailbox.DisplayName = displayName.Trim();
        mailbox.PasswordPbkdf2 = string.Empty;   // empty keeps the stored hash
        await this.store.UpsertMailboxAsync(mailbox, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// One page of a folder: all messages, only unread, or only read (rc.11,
    /// item 7), with the total for that choice.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="folderId">The folder.</param>
    /// <param name="show">all, unread or read; anything else means all.</param>
    /// <param name="page">Zero-based page.</param>
    /// <param name="pageSize">Messages per page.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The page and the total it is part of.</returns>
    public async Task<(IReadOnlyList<MessageRow> Items, long Total)> ListFilteredAsync(Guid mailboxId, Guid folderId, string? show, int page, int pageSize, CancellationToken ct = default)
    {
        bool? seen = show switch
        {
            "unread" => false,
            "read" => true,
            _ => null,
        };
        if (seen is null)
        {
            return await this.ListMessagesAsync(mailboxId, folderId, page, pageSize, ct).ConfigureAwait(false);
        }
        int size = Math.Clamp(pageSize, 1, 200);
        IReadOnlyList<MessageRow> items = await this.store.ListMessagesBySeenAsync(mailboxId, folderId, seen.Value, size, Math.Max(0, page) * size, ct).ConfigureAwait(false);
        long unread = await this.store.CountUnreadAsync(mailboxId, folderId, ct).ConfigureAwait(false);
        long total = seen.Value ? await this.store.CountMessagesAsync(mailboxId, folderId, ct).ConfigureAwait(false) - unread : unread;
        return (items, total);
    }

    /// <summary>Remember how many messages the person sees per page (rc.11, item 14).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="pageSize">One of <see cref="MailboxPreferences.PageSizes"/>; anything else keeps 50.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when saved.</returns>
    public Task<bool> SetPageSizeAsync(Guid mailboxId, int pageSize, CancellationToken ct = default) =>
        this.ChangePreferencesAsync(mailboxId, p => p.PageSize = pageSize, ct);

    /// <summary>
    /// Save language, time zone, date format and week start (rc.11, items 36 and
    /// 41). A language the owner has not switched on is refused and the current
    /// one kept (D-92); anything else unknown falls back to its default.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="language">A language code.</param>
    /// <param name="timeZone">An IANA time zone.</param>
    /// <param name="dateFormat">One of <see cref="MailboxPreferences.DateFormats"/>.</param>
    /// <param name="weekStart">monday or sunday.</param>
    /// <param name="words">The languages switched on.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The preferences as saved, or null when the mailbox is unknown.</returns>
    public async Task<MailboxPreferences?> SetLanguageAndTimeAsync(Guid mailboxId, string? language, string? timeZone, string? dateFormat, string? weekStart, Words words, CancellationToken ct = default) =>
        await this.SetLanguageAndTimeAsync(mailboxId, language, timeZone, dateFormat, weekStart, words, false, ct).ConfigureAwait(false);

    /// <summary>Save language and time, where an operator may also choose a language in preview (rc.15, item 41).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="language">A language code.</param>
    /// <param name="timeZone">An IANA zone.</param>
    /// <param name="dateFormat">A date format.</param>
    /// <param name="weekStart">The first day of the week.</param>
    /// <param name="words">The words built into the program.</param>
    /// <param name="allowPreview">True for an operator: a language not yet switched on may be chosen.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The preferences as saved, or null when the mailbox is unknown.</returns>
    public async Task<MailboxPreferences?> SetLanguageAndTimeAsync(Guid mailboxId, string? language, string? timeZone, string? dateFormat, string? weekStart, Words words, bool allowPreview, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(words);
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        if (context is null)
        {
            return null;
        }
        MailboxPreferences p = MailboxPreferences.Of(context.Value.Mailbox);
        string languageBefore = p.Language;
        if (language is not null && (words.IsEnabled(language.Trim().ToLowerInvariant()) || (allowPreview && words.IsPreview(language.Trim().ToLowerInvariant()))))
        {
            p.Language = language.Trim().ToLowerInvariant();
        }
        if (!string.Equals(languageBefore, p.Language, StringComparison.Ordinal))
        {
            // The person chose a language of their own: an organisation's later change of its
            // default reaches them only when its administrator says "everyone" (D-131).
            MailSettings s = await this.GetMailSettingsAsync(mailboxId, ct).ConfigureAwait(false);
            if (!s.LanguageChosen)
            {
                s.LanguageChosen = true;
                await this.WriteDocumentAsync(mailboxId, MailSettingsKind, s, ct).ConfigureAwait(false);
            }
        }
        p.TimeZone = timeZone ?? p.TimeZone;
        p.DateFormat = dateFormat ?? p.DateFormat;
        p.WeekStart = weekStart ?? p.WeekStart;
        MailboxPreferences saved = p.Normalized();
        return await this.store.SetMailboxPreferencesAsync(mailboxId, saved, ct).ConfigureAwait(false) ? saved : null;
    }

    /// <summary>Save the layout (three, focus, list) and density (comfortable, compact) - each only when given (rc.11, item 45).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="layout">three, focus or list; null keeps the current one.</param>
    /// <param name="density">comfortable or compact; null keeps the current one.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when saved.</returns>
    public Task<bool> SetLayoutAsync(Guid mailboxId, string? layout, string? density, CancellationToken ct = default) =>
        this.ChangePreferencesAsync(
            mailboxId,
            p =>
            {
                if (layout is not null)
                {
                    p.Layout = layout;
                }
                if (density is not null)
                {
                    p.Density = density;
                }
            },
            ct);

    /// <summary>Record that the welcome screen has been answered, so it is never shown again (rc.11, D-105).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when saved.</returns>
    public Task<bool> SetWelcomeDoneAsync(Guid mailboxId, CancellationToken ct = default) =>
        this.ChangePreferencesAsync(mailboxId, p => p.WelcomeDone = true, ct);

    /// <summary>Turn the new-mail sound on or off for this person (rc.11, item 11).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="on">True to play the sound.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when saved.</returns>
    public Task<bool> SetNewMailSoundAsync(Guid mailboxId, bool on, CancellationToken ct = default) =>
        this.ChangePreferencesAsync(mailboxId, p => p.NewMailSound = on, ct);

    /// <summary>
    /// What the page checks every 30 seconds for live update (rc.11, item 11):
    /// the INBOX unread count and when the newest INBOX message arrived.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The unread count and the newest arrival, if any.</returns>
    public async Task<(long Unread, DateTimeOffset? Newest)> InboxStateAsync(Guid mailboxId, CancellationToken ct = default)
    {
        FolderRow? inbox = await this.GetFolderAsync(mailboxId, FolderRow.Inbox, ct).ConfigureAwait(false);
        if (inbox is null)
        {
            return (0, null);
        }
        long unread = await this.store.CountUnreadAsync(mailboxId, inbox.Id, ct).ConfigureAwait(false);
        IReadOnlyList<MessageRow> newest = await this.store.ListMessagesAsync(mailboxId, inbox.Id, 1, 0, ct).ConfigureAwait(false);
        return (unread, newest.Count > 0 ? newest[0].ReceivedAt : null);
    }

    private async Task<bool> ChangePreferencesAsync(Guid mailboxId, Action<MailboxPreferences> change, CancellationToken ct)
    {
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        if (context is null)
        {
            return false;
        }
        MailboxPreferences preferences = MailboxPreferences.Of(context.Value.Mailbox);
        change(preferences);
        return await this.store.SetMailboxPreferencesAsync(mailboxId, preferences, ct).ConfigureAwait(false);
    }

    /// <summary>Remember whether the person keeps the rail folded to icons (rc.11).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="folded">True to show icons only.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when saved.</returns>
    public Task<bool> SetRailFoldedAsync(Guid mailboxId, bool folded, CancellationToken ct = default) =>
        this.ChangePreferencesAsync(mailboxId, p =>
        {
            p.RailFolded = folded;
            p.RailChosen = true;
        }, ct);

    /// <summary>Set the webmail theme for this mailbox. Returns the error to show, or null.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="theme">One of <see cref="Themes"/>.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> SetThemeAsync(Guid mailboxId, string theme, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(theme);
        string t = theme.Trim().ToLowerInvariant();
        if (!Themes.Contains(t))
        {
            return "That is not one of the available themes.";
        }
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        if (context is null)
        {
            return "Mailbox is not available.";
        }
        MailboxRow mailbox = context.Value.Mailbox;
        string before = MailboxRow.NormalizeTheme(mailbox.Theme);
        if (string.Equals(before, t, StringComparison.Ordinal))
        {
            return null;
        }
        mailbox.Theme = t;
        mailbox.PasswordPbkdf2 = string.Empty;
        await this.store.UpsertMailboxAsync(mailbox, ct).ConfigureAwait(false);

        // DES-11 D7: the person chose a colour of their own; an organisation's later change of
        // its default reaches them only when its administrator says "everyone".
        // Light or dark alone is not a colour of one's own.
        MailSettings s = await this.GetMailSettingsAsync(mailboxId, ct).ConfigureAwait(false);
        if (!s.ThemeChosen && !string.Equals(ColourOf(before), ColourOf(t), StringComparison.Ordinal))
        {
            s.ThemeChosen = true;
            await this.WriteDocumentAsync(mailboxId, MailSettingsKind, s, ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>The colour part of a theme ("rose" of "rose-dark").</summary>
    /// <param name="theme">A theme.</param>
    /// <returns>The colour.</returns>
    public static string ColourOf(string theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        string t = MailboxRow.NormalizeTheme(theme);
        int dash = t.LastIndexOf('-');
        return dash > 0 ? t[..dash] : t;
    }

    /// <summary>The first text/plain part of a MIME tree, or empty.</summary>
    /// <param name="entity">Root entity, or null.</param>
    public static string FirstPlainText(MimeEntity? entity)
    {
        if (entity is MimePart part)
        {
            return part.ContentType.MimeType == "text/plain" ? part.GetBodyAsText() : string.Empty;
        }
        if (entity is MimeMultipart multi)
        {
            foreach (MimeEntity child in multi.Parts)
            {
                string t = FirstPlainText(child);
                if (t.Length > 0)
                {
                    return t;
                }
            }
        }
        return string.Empty;
    }
}

/// <summary>rc.15 (items 56 and 58): more ways a dashboard number narrows a search.</summary>
/// <param name="Category">Only this category, or null.</param>
/// <param name="Uncategorised">Only messages without a category.</param>
/// <param name="BiggestFirst">The biggest messages first instead of the newest.</param>
/// <param name="Only">Only these messages (those rescued from Junk), or null.</param>
/// <param name="Sort">The order the person chose (owner, 9 Oct 2026); null or "newest" for newest first.</param>
public sealed record SearchNarrowing(Guid? Category = null, bool Uncategorised = false, bool BiggestFirst = false, IReadOnlySet<Guid>? Only = null, string? Sort = null)
{
    /// <summary>No narrowing.</summary>
    public static SearchNarrowing None { get; } = new();

    /// <summary>True when nothing is narrowed.</summary>
    public bool IsNone => this.Category is null && !this.Uncategorised && !this.BiggestFirst && this.Only is null && MailboxService.SortOf(this.Sort) == MailboxService.NewestFirst;
}
