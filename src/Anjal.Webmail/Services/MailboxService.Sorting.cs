using System.Globalization;
using Anjal.Mime;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>
/// Sorting a folder or search results (owner, 9 Oct 2026): newest first (the usual order),
/// oldest first, by sender (by recipient in Sent, Drafts and Scheduled), by subject, biggest
/// first, unread first or with attachments first. The choice is remembered for each folder,
/// and for search; a list not in date order shows no Today and Yesterday headings.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The usual order.</summary>
    public const string NewestFirst = "newest";

    /// <summary>The key under which search remembers its order.</summary>
    public const string SearchSortKey = "(search)";

    /// <summary>Every order offered, in the order the Sort menu lists them.</summary>
    public static readonly IReadOnlyList<string> SortChoices = new[] { "newest", "oldest", "sender", "subject", "size", "unread", "attachments" };

    private static readonly CompareInfo NameOrder = CultureInfo.InvariantCulture.CompareInfo;

    /// <summary>A known order, or newest first.</summary>
    /// <param name="sort">What was asked for.</param>
    /// <returns>One of <see cref="SortChoices"/>.</returns>
    public static string SortOf(string? sort) => sort is not null && SortChoices.Contains(sort) ? sort : NewestFirst;

    /// <summary>True when the order is by date, so the list keeps its Today and Yesterday headings.</summary>
    /// <param name="sort">The order.</param>
    /// <returns>Whether the day headings are shown.</returns>
    public static bool SortsByDate(string sort) => sort is "newest" or "oldest";

    /// <summary>The order the person chose for a folder (or for search), newest first until they choose.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="folder">The folder's name, or <see cref="SearchSortKey"/>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The order.</returns>
    public async Task<string> SortForAsync(Guid mailboxId, string folder, CancellationToken ct = default)
    {
        MailSettings s = await this.GetMailSettingsAsync(mailboxId, ct).ConfigureAwait(false);
        return s.Sorts.TryGetValue(folder ?? string.Empty, out string? sort) ? SortOf(sort) : NewestFirst;
    }

    /// <summary>Remember the order for a folder (or for search).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="folder">The folder's name, or <see cref="SearchSortKey"/>.</param>
    /// <param name="sort">The order; an unknown one means newest first.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The order kept.</returns>
    public async Task<string> SetSortAsync(Guid mailboxId, string folder, string? sort, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        string kept = SortOf(sort);
        MailSettings s = await this.GetMailSettingsAsync(mailboxId, ct).ConfigureAwait(false);
        if (kept == NewestFirst)
        {
            s.Sorts.Remove(folder);
        }
        else
        {
            s.Sorts[folder] = kept;
        }
        await this.WriteDocumentAsync(mailboxId, MailSettingsKind, s, ct).ConfigureAwait(false);
        return kept;
    }

    /// <summary>A page of a folder in the chosen order, with All, Unread or Read applied.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="folderId">The folder.</param>
    /// <param name="show">all, unread or read.</param>
    /// <param name="sort">The order.</param>
    /// <param name="outgoing">True for Sent, Drafts and Scheduled: by name means by recipient.</param>
    /// <param name="page">Zero-based page.</param>
    /// <param name="pageSize">Messages per page.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The page and the total it is part of.</returns>
    public async Task<(IReadOnlyList<MessageRow> Items, long Total)> ListSortedAsync(Guid mailboxId, Guid folderId, string? show, string? sort, bool outgoing, int page, int pageSize, CancellationToken ct = default)
    {
        string order = SortOf(sort);
        if (order == NewestFirst)
        {
            return await this.ListFilteredAsync(mailboxId, folderId, show, page, pageSize, ct).ConfigureAwait(false);
        }
        bool? seen = show switch
        {
            "unread" => false,
            "read" => true,
            _ => null,
        };
        int size = Math.Clamp(pageSize, 1, 200);
        int offset = Math.Max(0, page) * size;
        long unread = await this.store.CountUnreadAsync(mailboxId, folderId, ct).ConfigureAwait(false);
        long all = await this.store.CountMessagesAsync(mailboxId, folderId, ct).ConfigureAwait(false);
        long total = seen switch
        {
            true => all - unread,
            false => unread,
            _ => all,
        };
        if (order != "sender")
        {
            return (await this.store.ListMessagesSortedAsync(mailboxId, folderId, seen, order, size, offset, ct).ConfigureAwait(false), total);
        }
        // By name: the names are decoded first (a Tamil or Malayalam name arrives encoded), then put in order.
        IReadOnlyList<MessageNames> names = await this.store.ListMessageNamesAsync(mailboxId, folderId, seen, ct).ConfigureAwait(false);
        List<Guid> pageIds = names
            .Select(n => (n.Id, n.ReceivedAt, Name: outgoing ? RecipientName(n.ToHeader) : SenderName(n.FromHeader, n.EnvelopeFrom)))
            .OrderBy(n => n.Name, Comparer<string>.Create((a, b) => NameOrder.Compare(a, b, CompareOptions.IgnoreCase)))
            .ThenByDescending(n => n.ReceivedAt)
            .ThenByDescending(n => n.Id)
            .Skip(offset)
            .Take(size)
            .Select(n => n.Id)
            .ToList();
        IReadOnlyList<MessageRow> rows = await this.store.ListMessagesByIdsAsync(mailboxId, pageIds, ct).ConfigureAwait(false);
        var byId = rows.ToDictionary(r => r.Id);
        return (pageIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList(), names.Count);
    }

    /// <summary>Put search results in the chosen order (newest first is how they come).</summary>
    /// <param name="rows">The results, newest first.</param>
    /// <param name="sort">The order.</param>
    /// <returns>The results in order.</returns>
    public static IReadOnlyList<MessageRow> Sorted(IReadOnlyList<MessageRow> rows, string? sort)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var cmp = Comparer<string>.Create((a, b) => NameOrder.Compare(a, b, CompareOptions.IgnoreCase));
        return SortOf(sort) switch
        {
            "oldest" => rows.Reverse().ToList(),
            "sender" => rows.OrderBy(m => SenderName(m.FromHeader, m.EnvelopeFrom), cmp).ToList(),
            "subject" => rows.OrderBy(m => SubjectKey(m.Subject), cmp).ToList(),
            "size" => rows.OrderByDescending(m => m.SizeBytes).ToList(),
            "unread" => rows.OrderBy(m => m.Seen).ToList(),
            "attachments" => rows.OrderByDescending(m => m.HasAttachments).ToList(),
            _ => rows,
        };
    }

    /// <summary>The name a list shows for a sender: the display name, else the address.</summary>
    /// <param name="fromHeader">The From header as stored.</param>
    /// <param name="envelopeFrom">The envelope sender.</param>
    /// <returns>The name.</returns>
    public static string SenderName(string fromHeader, string envelopeFrom)
    {
        string decoded = EncodedWordDecoder.Decode(fromHeader ?? string.Empty);
        var parsed = AddressParser.Parse(decoded);
        if (parsed.Count > 0)
        {
            return parsed[0].DisplayName.Length > 0 ? parsed[0].DisplayName : parsed[0].Address;
        }
        return string.IsNullOrWhiteSpace(decoded) ? envelopeFrom ?? string.Empty : decoded;
    }

    /// <summary>The name a list shows for the first recipient: the display name, else the address.</summary>
    /// <param name="toHeader">The To header as stored.</param>
    /// <returns>The name.</returns>
    public static string RecipientName(string toHeader)
    {
        string decoded = EncodedWordDecoder.Decode(toHeader ?? string.Empty);
        var parsed = AddressParser.Parse(decoded);
        return parsed.Count == 0 ? decoded : parsed[0].DisplayName.Length > 0 ? parsed[0].DisplayName : parsed[0].Address;
    }

    private static string SubjectKey(string subject)
    {
        string s = (subject ?? string.Empty).TrimStart();
        while (true)
        {
            int colon = s.IndexOf(':', StringComparison.Ordinal);
            if (colon is < 2 or > 3 || !(s[..colon].Trim().ToLowerInvariant() is "re" or "fw" or "fwd" or "aw"))
            {
                return s;
            }
            s = s[(colon + 1)..].TrimStart();
        }
    }
}
