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

    /// <summary>Themes a mailbox may choose.</summary>
    public static readonly IReadOnlyList<string> Themes = new[] { "paper", "ink", "postcard", "midnight" };

    /// <summary>Minimum length of a new password.</summary>
    public const int MinimumPasswordLength = 12;

    // ---------------- Search ----------------

    /// <summary>
    /// Search one folder or the whole mailbox. Returns the page and the
    /// total number of matches.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="folderId">Folder to search, or null for all folders.</param>
    /// <param name="query">Case-insensitive substring; empty matches everything.</param>
    /// <param name="page">Zero-based page.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<(IReadOnlyList<MessageRow> Items, long Total)> SearchAsync(Guid mailboxId, Guid? folderId, string query, int page, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        int offset = Math.Max(0, page) * PageSize;
        IReadOnlyList<MessageRow> items = await this.store.SearchMessagesAsync(mailboxId, folderId, query, PageSize, offset, ct).ConfigureAwait(false);
        long total = await this.store.CountSearchAsync(mailboxId, folderId, query, ct).ConfigureAwait(false);
        return (items, total);
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
        if (sent is null)
        {
            return Array.Empty<ContactSuggestion>();
        }

        // 500 most recent sent messages is plenty to learn who you write to.
        IReadOnlyList<MessageRow> rows = await this.store.ListMessagesAsync(mailboxId, sent.Id, 500, 0, ct).ConfigureAwait(false);
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
    public async Task<Guid?> SaveDraftAsync(Guid mailboxId, ComposeRequest request, CancellationToken ct = default)
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
        FolderRow drafts = await this.store.EnsureFolderAsync(mailbox.Id, "Drafts", ct).ConfigureAwait(false);
        MaildirWriteResult written = await this.maildir.WriteAsync(tenant.Slug, mailbox.Address, drafts.Name, raw, ct).ConfigureAwait(false);
        string? seenPath = this.maildir.SetFlags(tenant.Slug, mailbox.Address, drafts.Name, written.RelativePath, seen: true, flagged: false, answered: false);

        MessageRow saved = await this.store.SaveMessageAsync(new MessageRow
        {
            MailboxId = mailbox.Id,
            FolderId = drafts.Id,
            MaildirFile = seenPath ?? written.RelativePath,
            EnvelopeFrom = mailbox.Address,
            FromHeader = FormatFrom(mailbox),
            ToHeader = request.To.Trim(),
            Subject = request.Subject.Trim(),
            DateHeader = FormatDate(DateTimeOffset.UtcNow),
            SizeBytes = written.SizeBytes,
            Seen = true,
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
    /// Load a draft back into a compose form. Returns null if the message
    /// is not a draft of this mailbox.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="draftId">The draft.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<ComposeRequest?> LoadDraftAsync(Guid mailboxId, Guid draftId, CancellationToken ct = default)
    {
        (MessageRow Row, FolderRow Folder, byte[] Raw)? loaded = await this.ReadRawAsync(mailboxId, draftId, ct).ConfigureAwait(false);
        if (loaded is null || !string.Equals(loaded.Value.Folder.Name, "Drafts", StringComparison.Ordinal))
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
            InReplyTo = parsed.Headers.Get("In-Reply-To")?.Trim('<', '>', ' ') ?? string.Empty,
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
            Body = QuoteForReply(from, date, body, kind == PrefillKind.Forward, to, cc, subject),
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
    public static string QuoteForReply(string from, string date, string body, bool forward, string to = "", string cc = "", string subject = "")
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

        sb.Append(AttributionLine(from, date)).Append('\n');
        foreach (string line in body.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd().Split('\n'))
        {
            sb.Append(line.Length == 0 ? ">" : "> " + line).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// The line above a quoted reply: <c>On 20 September 2026 at 14:32, Name wrote:</c>.
    /// Falls back to just the name when the date cannot be parsed.
    /// </summary>
    /// <param name="from">Original From header.</param>
    /// <param name="date">Original Date header.</param>
    public static string AttributionLine(string from, string date)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(date);
        IReadOnlyList<MailAddress> parsed = AddressParser.Parse(from);
        string who = parsed.Count > 0 && parsed[0].DisplayName.Length > 0 ? parsed[0].DisplayName
            : parsed.Count > 0 ? parsed[0].Address
            : from;
        if (TryParseRfc5322(date, out DateTimeOffset when))
        {
            return $"On {when:d MMMM yyyy} at {when:HH:mm}, {who} wrote:";
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
        foreach (Guid id in messageIds)
        {
            MessageRow? row = await this.GetOwnedRowAsync(mailboxId, id, ct).ConfigureAwait(false);
            if (row is null)
            {
                continue;
            }
            object? result = action switch
            {
                BulkAction.Trash => await this.MoveAsync(mailboxId, id, "Trash", ct).ConfigureAwait(false),
                BulkAction.MarkRead => await this.SetFlagsAsync(mailboxId, id, true, row.Flagged, row.Answered, ct).ConfigureAwait(false),
                BulkAction.MarkUnread => await this.SetFlagsAsync(mailboxId, id, false, row.Flagged, row.Answered, ct).ConfigureAwait(false),
                BulkAction.ReportSpam => await this.ReportSpamAsync(mailboxId, id, ct).ConfigureAwait(false),
                _ => await this.MarkNotSpamAsync(mailboxId, id, ct).ConfigureAwait(false),
            };
            if (result is not null)
            {
                changed++;
            }
        }
        return changed;
    }

    /// <summary>Mark every message in a folder seen. Returns how many changed.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="folderId">The folder.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<int> MarkFolderReadAsync(Guid mailboxId, Guid folderId, CancellationToken ct = default)
    {
        int changed = 0;
        while (true)
        {
            IReadOnlyList<MessageRow> batch = await this.store.ListMessagesAsync(mailboxId, folderId, 200, 0, ct).ConfigureAwait(false);
            var unread = new List<MessageRow>();
            foreach (MessageRow m in batch)
            {
                if (!m.Seen)
                {
                    unread.Add(m);
                }
            }
            if (unread.Count == 0)
            {
                return changed;
            }
            foreach (MessageRow m in unread)
            {
                await this.SetFlagsAsync(mailboxId, m.Id, true, m.Flagged, m.Answered, ct).ConfigureAwait(false);
                changed++;
            }
            if (batch.Count < 200)
            {
                return changed;
            }
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
        mailbox.Theme = t;
        mailbox.PasswordPbkdf2 = string.Empty;
        await this.store.UpsertMailboxAsync(mailbox, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Change the mailbox password after checking the current one. Returns
    /// the error to show, or null on success. The session stays signed in.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="currentPassword">The current password.</param>
    /// <param name="newPassword">The new password.</param>
    /// <param name="confirmPassword">The new password again.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> ChangePasswordAsync(Guid mailboxId, string currentPassword, string newPassword, string confirmPassword, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(currentPassword);
        ArgumentNullException.ThrowIfNull(newPassword);
        ArgumentNullException.ThrowIfNull(confirmPassword);
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        if (context is null)
        {
            return "Mailbox is not available.";
        }
        MailboxRow mailbox = context.Value.Mailbox;
        if (mailbox.PasswordPbkdf2.Length == 0 || !Anjal.Smtp.Pbkdf2Hasher.Verify(currentPassword, mailbox.PasswordPbkdf2))
        {
            return "The current password is not correct.";
        }
        if (newPassword.Length < MinimumPasswordLength)
        {
            return $"The new password must be at least {MinimumPasswordLength} characters.";
        }
        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
        {
            return "The two new passwords do not match.";
        }
        if (string.Equals(newPassword, currentPassword, StringComparison.Ordinal))
        {
            return "The new password is the same as the current one.";
        }
        mailbox.PasswordPbkdf2 = Anjal.Smtp.Pbkdf2Hasher.Hash(newPassword);
        await this.store.UpsertMailboxAsync(mailbox, ct).ConfigureAwait(false);
        return null;
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
