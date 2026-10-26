using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Anjal.Mime;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>One message of a conversation, as the envelope lists it.</summary>
/// <param name="Id">The message.</param>
/// <param name="Folder">Its folder.</param>
/// <param name="From">Who wrote it.</param>
/// <param name="At">When it arrived or was sent.</param>
/// <param name="Seen">Read.</param>
public sealed record ConversationItem(Guid Id, string Folder, string From, DateTimeOffset At, bool Seen)
{
    /// <summary>
    /// rc.24.1: the mail's Message-ID, so two copies of one mail (a mail sent to oneself sits in Sent and in
    /// the Inbox) count once; empty when it has none.
    /// </summary>
    public string MessageKey { get; init; } = string.Empty;
}

/// <summary>
/// Conversations (rc.12, UX-04): replies grouped. A conversation is the
/// messages - in every folder, Sent included - whose subjects are the same
/// once "Re:", "Fwd:" and their kind are taken off. The list shows how many
/// a conversation holds; the envelope lists them all. Worked out from the
/// newest 3,000 messages and kept for a few seconds, so lists stay quick.
/// </summary>
public sealed partial class MailboxService
{
    private const int ConversationWindow = 3000;

    private static readonly TimeSpan ConversationCacheFor = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<Guid, (DateTimeOffset Built, IReadOnlyDictionary<string, IReadOnlyList<ConversationItem>> Index)> conversations = new();

    [GeneratedRegex(@"^\s*((re|fw|fwd|aw|wg|sv|vs|antw|tr)\s*(\[\d+\])?\s*:\s*)+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex ReplyPrefixRegex();

    /// <summary>A subject without its reply and forward prefixes, case and spacing folded: the conversation's key.</summary>
    /// <param name="subject">The subject.</param>
    public static string ConversationKey(string? subject)
    {
        string s = EncodedWordDecoder.Decode(subject ?? string.Empty);
        s = ReplyPrefixRegex().Replace(s, string.Empty);
        s = Regex.Replace(s, @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)).Trim();
        return s.ToLowerInvariant();
    }

    /// <summary>The conversation a message belongs to, oldest first (only itself when alone or untitled).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="message">The message.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyList<ConversationItem>> ConversationOfAsync(Guid mailboxId, MessageRow message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        string key = ConversationKey(message.Subject);
        IReadOnlyDictionary<string, IReadOnlyList<ConversationItem>> index = await this.ConversationIndexAsync(mailboxId, ct).ConfigureAwait(false);
        return key.Length > 0 && index.TryGetValue(key, out IReadOnlyList<ConversationItem>? items) ? items : Array.Empty<ConversationItem>();
    }

    /// <summary>How many messages each subject's conversation holds, for a list's count bubbles.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<Func<MessageRow, int>> ConversationCountsAsync(Guid mailboxId, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, IReadOnlyList<ConversationItem>> index = await this.ConversationIndexAsync(mailboxId, ct).ConfigureAwait(false);
        return m =>
        {
            string key = ConversationKey(m.Subject);
            return key.Length > 0 && index.TryGetValue(key, out IReadOnlyList<ConversationItem>? items) ? ConversationGrouping.Distinct(items) : 1;
        };
    }

    /// <summary>
    /// rc.24 (item 68, brief 10a): how a folder's list groups conversations. A message leads its
    /// conversation in a folder when no newer message of the same conversation is in that folder;
    /// only the leads are listed. The row opens the conversation at its newest unread mail
    /// (any folder), or at the lead itself when all are read.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="folderName">The folder listed.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The grouping.</returns>
    public async Task<ConversationGrouping> ConversationGroupingAsync(Guid mailboxId, string folderName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderName);
        IReadOnlyDictionary<string, IReadOnlyList<ConversationItem>> index = await this.ConversationIndexAsync(mailboxId, ct).ConfigureAwait(false);
        return new ConversationGrouping(index, folderName);
    }

    /// <summary>Mark every message of a conversation read (the board's "Mark all in conversation read").</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">Any message of it.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<int> MarkConversationReadAsync(Guid mailboxId, Guid messageId, CancellationToken ct = default)
    {
        MessageRow? row = await this.GetOwnedRowAsync(mailboxId, messageId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return 0;
        }
        int changed = 0;
        foreach (ConversationItem item in await this.ConversationOfAsync(mailboxId, row, ct).ConfigureAwait(false))
        {
            if (!item.Seen)
            {
                MessageRow? other = await this.GetOwnedRowAsync(mailboxId, item.Id, ct).ConfigureAwait(false);
                if (other is not null && await this.SetFlagsAsync(mailboxId, other.Id, true, other.Flagged, other.Answered, ct).ConfigureAwait(false) is not null)
                {
                    changed++;
                }
            }
        }
        this.conversations.TryRemove(mailboxId, out _);
        return changed;
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<ConversationItem>>> ConversationIndexAsync(Guid mailboxId, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (this.conversations.TryGetValue(mailboxId, out (DateTimeOffset Built, IReadOnlyDictionary<string, IReadOnlyList<ConversationItem>> Index) cached)
            && now - cached.Built < ConversationCacheFor)
        {
            return cached.Index;
        }
        IReadOnlyDictionary<Guid, string> folderNames = await this.FolderNamesAsync(mailboxId, ct).ConfigureAwait(false);
        IReadOnlyList<MessageRow> rows = await this.store.ListMessagesAsync(mailboxId, null, ConversationWindow, 0, ct).ConfigureAwait(false);
        var index = rows
            .Where(r => !(folderNames.TryGetValue(r.FolderId, out string? f) && (f == "Drafts" || f == ScheduledFolder || f == "Trash" || f == Anjal.Mailbox.MailboxSink.JunkFolder)))
            .GroupBy(r => ConversationKey(r.Subject))
            .Where(g => g.Key.Length > 0)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ConversationItem>)g.OrderBy(r => r.ReceivedAt)
                    .Select(r => new ConversationItem(r.Id, folderNames.TryGetValue(r.FolderId, out string? n) ? n : string.Empty, ConversationSender(r), r.ReceivedAt, r.Seen)
                    {
                        MessageKey = r.MessageId.Trim().Trim('<', '>').ToLowerInvariant(),
                    })
                    .ToList(),
                StringComparer.Ordinal);
        this.conversations[mailboxId] = (now, index);
        return index;
    }

    private static string ConversationSender(MessageRow r)
    {
        IReadOnlyList<MailAddress> parsed = AddressParser.Parse(EncodedWordDecoder.Decode(r.FromHeader));
        return parsed.Count > 0 ? (parsed[0].DisplayName.Length > 0 ? parsed[0].DisplayName : parsed[0].Address) : r.EnvelopeFrom;
    }
}

/// <summary>A folder list's view of its conversations (rc.24, item 68): which rows lead, their counts, and where each opens.</summary>
public sealed class ConversationGrouping
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ConversationItem>> index;
    private readonly string folder;

    /// <summary>Group by this index, for this folder.</summary>
    /// <param name="index">The conversations by key.</param>
    /// <param name="folder">The folder listed.</param>
    public ConversationGrouping(IReadOnlyDictionary<string, IReadOnlyList<ConversationItem>> index, string folder)
    {
        this.index = index ?? throw new ArgumentNullException(nameof(index));
        this.folder = folder ?? throw new ArgumentNullException(nameof(folder));
    }

    private IReadOnlyList<ConversationItem> Of(MessageRow m)
    {
        string key = MailboxService.ConversationKey(m.Subject);
        return key.Length > 0 && this.index.TryGetValue(key, out IReadOnlyList<ConversationItem>? items) && items.Any(i => i.Id == m.Id)
            ? items
            : Array.Empty<ConversationItem>();
    }

    /// <summary>True when this message is listed: no newer message of its conversation is in this folder.</summary>
    /// <param name="m">The message.</param>
    public bool Leads(MessageRow m)
    {
        ArgumentNullException.ThrowIfNull(m);
        IReadOnlyList<ConversationItem> items = this.Of(m);
        return items.Count == 0 || !items.Any(i => i.Id != m.Id && i.Folder == this.folder && (i.At > m.ReceivedAt || (i.At == m.ReceivedAt && i.Id.CompareTo(m.Id) > 0)));
    }

    /// <summary>How many mails the conversation holds (1 when it stands alone).</summary>
    /// <param name="m">The message.</param>
    public int Count(MessageRow m)
    {
        ArgumentNullException.ThrowIfNull(m);
        IReadOnlyList<ConversationItem> items = this.Of(m);
        return items.Count == 0 ? 1 : Distinct(items);
    }

    /// <summary>How many different mails: copies of one mail (same Message-ID) count once (rc.24.1).</summary>
    /// <param name="items">The conversation.</param>
    /// <returns>The count.</returns>
    public static int Distinct(IReadOnlyList<ConversationItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Select(i => i.MessageKey.Length > 0 ? i.MessageKey : i.Id.ToString("N")).Distinct(StringComparer.Ordinal).Count();
    }

    /// <summary>The mail the row opens: the conversation's newest unread mail, or this one.</summary>
    /// <param name="m">The message.</param>
    public Guid OpensAt(MessageRow m)
    {
        ArgumentNullException.ThrowIfNull(m);
        ConversationItem? unread = this.Of(m).Where(i => !i.Seen && i.Folder is not ("Sent" or "Drafts")).OrderByDescending(i => i.At).FirstOrDefault();
        return unread?.Id ?? m.Id;
    }

    /// <summary>True when the open message is in this row's conversation.</summary>
    /// <param name="m">The row's message.</param>
    /// <param name="openId">The open message, if any.</param>
    public bool Holds(MessageRow m, Guid? openId)
    {
        ArgumentNullException.ThrowIfNull(m);
        return openId is Guid id && (id == m.Id || this.Of(m).Any(i => i.Id == id));
    }
}
