using Anjal.Mime;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>A mail the person can choose in the picker (the editor's "Reply to another mail", a draft's "Reply to a mail instead").</summary>
/// <param name="Id">The mail.</param>
/// <param name="Who">The sender, or for a mail the person sent or is writing, the people it goes to.</param>
/// <param name="Subject">Its subject.</param>
/// <param name="At">When it arrived or was written.</param>
/// <param name="Folder">The folder it is in.</param>
public sealed record PickItem(Guid Id, string Who, string Subject, DateTimeOffset At, string Folder);

/// <summary>
/// rc.29 part 2, the owner's check (17 Nov 2026): where an edited file can go besides the mail it came from - a reply
/// to any other mail, a draft the person is already writing, or back on the draft it came from - and turning a
/// draft into a reply to a mail chosen later. Nothing here changes a mail that is not a draft of this mailbox.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The most mails the picker offers at once.</summary>
    public const int PickLimit = 20;

    // Folders a mail is never chosen from to answer: what is not sent yet, and what was thrown away.
    private static readonly HashSet<string> NotAnswerable = new(StringComparer.Ordinal) { "Drafts", ScheduledFolder, "Outbox", "Trash", Anjal.Mailbox.MailboxSink.JunkFolder };

    /// <summary>
    /// The mails the picker offers: matching the words in every folder the person answers from (Inbox, Sent, Archive
    /// and their own folders - not Drafts, Scheduled, Outbox, Junk or Trash), newest first; or with
    /// <paramref name="drafts"/>, the person's drafts.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="query">Words to find; empty for the newest.</param>
    /// <param name="drafts">The drafts instead of the mail.</param>
    /// <param name="except">A mail not to offer (the draft being turned into a reply), or null.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>At most <see cref="PickLimit"/> mails.</returns>
    public async Task<IReadOnlyList<PickItem>> PickAsync(Guid mailboxId, string query, bool drafts, Guid? except = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        string q = query.Trim();
        if (q.Length > 200)
        {
            q = q[..200];
        }
        Dictionary<Guid, string> names = (await this.ListFoldersAsync(mailboxId, ct).ConfigureAwait(false)).ToDictionary(f => f.Id, f => f.Name);
        Guid? only = drafts ? names.FirstOrDefault(kv => kv.Value == "Drafts").Key : null;
        if (drafts && only == Guid.Empty)
        {
            return Array.Empty<PickItem>();
        }
        var found = new List<PickItem>();
        for (int page = 0; page < 5 && found.Count < PickLimit; page++)
        {
            (IReadOnlyList<MessageRow> rows, long _) = await this.SearchAsync(mailboxId, only, q, page, 60, ct).ConfigureAwait(false);
            foreach (MessageRow row in rows)
            {
                string folder = names.TryGetValue(row.FolderId, out string? n) ? n : string.Empty;
                if (row.Id == except || (drafts ? folder != "Drafts" : NotAnswerable.Contains(folder)))
                {
                    continue;
                }
                bool own = folder is "Sent" or "Drafts";
                string who = own ? SenderName(EncodedWordDecoder.Decode(row.ToHeader), string.Empty) : SenderName(row.FromHeader, row.EnvelopeFrom);
                found.Add(new PickItem(row.Id, who, row.Subject, row.ReceivedAt, folder));
                if (found.Count == PickLimit)
                {
                    break;
                }
            }
            if (rows.Count < 60)
            {
                break;
            }
        }
        return found;
    }

    /// <summary>
    /// Puts an edited file on a draft the person is writing: the draft keeps everything it had, its files in their
    /// order, and the new file is added at the end.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="draftId">The draft.</param>
    /// <param name="fileName">The new file's name.</param>
    /// <param name="file">The new file.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The draft, or the reason it could not be done.</returns>
    public async Task<(Guid? Draft, string? Problem)> AddToDraftAsync(Guid mailboxId, Guid draftId, string fileName, byte[] file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(file);
        return await this.RewriteDraftAsync(mailboxId, draftId, null, (fileName, file), ct).ConfigureAwait(false);
    }

    /// <summary>Puts an edited file back on the draft it came from, in the place of the file it was made from.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="draftId">The draft.</param>
    /// <param name="index">The file it replaces.</param>
    /// <param name="fileName">The new file's name.</param>
    /// <param name="file">The new file.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The draft, or the reason it could not be done.</returns>
    public async Task<(Guid? Draft, string? Problem)> ReplaceOnDraftAsync(Guid mailboxId, Guid draftId, int index, string fileName, byte[] file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(file);
        return await this.RewriteDraftAsync(mailboxId, draftId, index, (fileName, file), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns a draft into a reply (or a reply to all) to a mail chosen later: the draft's own words and files stay,
    /// it is addressed and titled as a reply, the original is quoted below, and it is threaded under that mail.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="draftId">The draft.</param>
    /// <param name="mailId">The mail it answers.</param>
    /// <param name="all">A reply to all.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The draft, or null when either is not what it should be.</returns>
    public async Task<Guid?> ReplyDraftToAsync(Guid mailboxId, Guid draftId, Guid mailId, bool all, CancellationToken ct = default)
    {
        if (draftId == mailId
            || await this.ReadRawAsync(mailboxId, mailId, ct).ConfigureAwait(false) is not { } target
            || NotAnswerable.Contains(target.Folder.Name)
            || await this.LoadDraftAsync(mailboxId, draftId, ct).ConfigureAwait(false) is not ComposeRequest request
            || await this.PrefillAsync(mailboxId, mailId, all ? PrefillKind.ReplyAll : PrefillKind.Reply, ct).ConfigureAwait(false) is not ComposePrefill prefill)
        {
            return null;
        }
        request.To = prefill.To;
        request.Cc = prefill.Cc;
        request.Subject = prefill.Subject;
        request.InReplyTo = prefill.InReplyTo;
        request.References = prefill.References;
        // What the person wrote stays first; the reply's quote of the original follows it.
        string quote = prefill.Body.Trim('\r', '\n');
        if (quote.Length > 0)
        {
            request.Body = request.Body.TrimEnd() + "\r\n\r\n" + quote;
            if (request.BodyHtml.Length > 0)
            {
                request.BodyHtml += "<br><br>" + System.Net.WebUtility.HtmlEncode(quote).Replace("\r\n", "<br>", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal);
            }
        }
        (Guid? draft, string? _) = await this.RewriteDraftAsync(mailboxId, draftId, null, null, ct, request).ConfigureAwait(false);
        return draft;
    }

    /// <summary>The place of a file on a draft, found by its name (the last one so named); null when it is not there.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="draftId">The draft.</param>
    /// <param name="fileName">The file's name.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The attachment's index, or null.</returns>
    public async Task<int?> DraftFileIndexAsync(Guid mailboxId, Guid draftId, string fileName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        IReadOnlyList<AttachmentView>? files = await this.ListAttachmentsAsync(mailboxId, draftId, ct).ConfigureAwait(false);
        AttachmentView? last = files?.LastOrDefault(a => string.Equals(a.FileName, fileName, StringComparison.Ordinal));
        return last?.Index;
    }

    // A draft saved again from the server: its own fields, its From address, and its files in order - one replaced
    // by the new file, or the new file added at the end. Only a draft of this mailbox; its size limit still holds.
    private async Task<(Guid? Draft, string? Problem)> RewriteDraftAsync(Guid mailboxId, Guid draftId, int? replace, (string Name, byte[] Bytes)? file, CancellationToken ct, ComposeRequest? prepared = null)
    {
        ComposeRequest? request = prepared ?? await this.LoadDraftAsync(mailboxId, draftId, ct).ConfigureAwait(false);
        if (request is null || await this.ReadRawAsync(mailboxId, draftId, ct).ConfigureAwait(false) is not { } raw)
        {
            return (null, "The file could not be saved.");
        }
        request.DraftId = draftId;
        string from = AddressOf(EncodedWordDecoder.Decode(raw.Row.FromHeader));
        if (from.Length > 0 && (await this.SendAsAddressesAsync(mailboxId, ct).ConfigureAwait(false)).Contains(from, StringComparer.OrdinalIgnoreCase))
        {
            request.FromAddress = from.ToLowerInvariant();
        }
        IReadOnlyList<AttachmentView> files = await this.ListAttachmentsAsync(mailboxId, draftId, ct).ConfigureAwait(false) ?? Array.Empty<AttachmentView>();
        if (replace is int r && !files.Any(a => a.Index == r))
        {
            return (null, "The file could not be saved.");
        }
        foreach (AttachmentView a in files)
        {
            if (a.Index == replace && file is { } swap)
            {
                request.Attachments.Add((swap.Name, "application/pdf", swap.Bytes));
                continue;
            }
            if (await this.GetAttachmentAsync(mailboxId, draftId, a.Index, ct).ConfigureAwait(false) is { } kept)
            {
                request.Attachments.Add((kept.View.FileName, kept.View.ContentType, kept.Bytes));
            }
        }
        if (replace is null && file is { } added)
        {
            request.Attachments.Add((added.Name, "application/pdf", added.Bytes));
        }
        if (await this.AddCarriedAttachmentsAsync(mailboxId, request, ct).ConfigureAwait(false) is string tooMuch)
        {
            return (null, tooMuch);
        }
        Guid? saved = await this.SaveDraftAsync(mailboxId, request, ct).ConfigureAwait(false);
        return saved is Guid id ? (id, null) : (null, "The file could not be saved.");
    }

    // The address in a From line ("Name <a@b>" or "a@b").
    private static string AddressOf(string header)
    {
        int open = header.LastIndexOf('<');
        int close = header.LastIndexOf('>');
        string address = open >= 0 && close > open ? header[(open + 1)..close] : header;
        return address.Trim();
    }
}
