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
public sealed record ConversationItem(Guid Id, string Folder, string From, DateTimeOffset At, bool Seen);

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
            return key.Length > 0 && index.TryGetValue(key, out IReadOnlyList<ConversationItem>? items) ? items.Count : 1;
        };
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
                    .Select(r => new ConversationItem(r.Id, folderNames.TryGetValue(r.FolderId, out string? n) ? n : string.Empty, ConversationSender(r), r.ReceivedAt, r.Seen))
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
