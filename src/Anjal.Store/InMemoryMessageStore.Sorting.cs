using System.Text.RegularExpressions;

namespace Anjal.Store;

/// <summary>Folders put in the order a person chooses (owner, 9 Oct 2026), in the in-memory store.</summary>
public sealed partial class InMemoryMessageStore
{
    private static readonly Regex ReplyPrefix = new(@"^\s*((re|fwd?|aw)\s*:\s*)+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, System.TimeSpan.FromSeconds(1));

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MessageRow>> ListMessagesSortedAsync(System.Guid mailboxId, System.Guid folderId, bool? seen, string sort, int limit, int offset, CancellationToken ct = default)
    {
        // Newest first, as the store keeps them; each order below is stable, so ties stay newest first.
        IEnumerable<MessageRow> all = (await this.ListMessagesAsync(mailboxId, folderId, int.MaxValue, 0, ct).ConfigureAwait(false))
            .Where(m => seen is null || m.Seen == seen.Value);
        IEnumerable<MessageRow> ordered = sort switch
        {
            "oldest" => all.Reverse(),
            "subject" => all.OrderBy(m => ReplyPrefix.Replace(m.Subject, string.Empty).ToLowerInvariant(), System.StringComparer.Ordinal),
            "size" => all.OrderByDescending(m => m.SizeBytes),
            "unread" => all.OrderBy(m => m.Seen),
            "attachments" => all.OrderByDescending(m => m.HasAttachments),
            _ => all,
        };
        return ordered.Skip(System.Math.Max(0, offset)).Take(System.Math.Max(0, limit)).ToList();
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<MessageNames>> ListMessageNamesAsync(System.Guid mailboxId, System.Guid folderId, bool? seen, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<MessageNames> result = this.mailboxMessages
                .Where(m => m.MailboxId == mailboxId && m.FolderId == folderId && (seen is null || m.Seen == seen.Value))
                .Select(m => new MessageNames(m.Id, m.FromHeader, m.ToHeader, m.EnvelopeFrom, m.ReceivedAt))
                .ToList();
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<MessageRow>> ListMessagesByIdsAsync(System.Guid mailboxId, IReadOnlyList<System.Guid> ids, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(ids);
        var wanted = new HashSet<System.Guid>(ids);
        lock (this.gate)
        {
            IReadOnlyList<MessageRow> result = this.mailboxMessages
                .Where(m => m.MailboxId == mailboxId && wanted.Contains(m.Id))
                .Select(Clone)
                .ToList();
            return Task.FromResult(result);
        }
    }
}
