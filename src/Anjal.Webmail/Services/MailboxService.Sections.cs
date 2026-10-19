using System.Globalization;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>One Inbox section (rc.22, item 55): a name, a saved search, and how many rows it shows.</summary>
public sealed class InboxSection
{
    /// <summary>Its name, for example "From the MD".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Its search: is:unread, is:flagged, from:name, has:attachment, newer:7d, and words.</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>Rows shown: 3, 5 or 10.</summary>
    public int Rows { get; set; } = 5;
}

/// <summary>Gmail-style sections above the Inbox (rc.22, item 55; owner 18 Oct 2026: not the default, an option in Settings), kept as the mailbox's "sections" document.</summary>
public sealed class InboxSections
{
    /// <summary>The mailbox document kind.</summary>
    public const string Kind = "sections";

    /// <summary>The row choices.</summary>
    public static readonly IReadOnlyList<int> RowChoices = new[] { 3, 5, 10 };

    /// <summary>Whether sections show.</summary>
    public bool On { get; set; }

    /// <summary>The sections, in order.</summary>
    public List<InboxSection> List { get; set; } = new();

    /// <summary>The two sections everyone starts with.</summary>
    public static List<InboxSection> Defaults() => new()
    {
        new InboxSection { Name = "Unread", Query = "is:unread", Rows = 5 },
        new InboxSection { Name = "Flagged", Query = "is:flagged", Rows = 5 },
    };
}

/// <summary>What one section shows: its rows, and how many matched in all.</summary>
/// <param name="Section">The section.</param>
/// <param name="Rows">The rows shown.</param>
/// <param name="Total">How many matched among recent Inbox mail.</param>
/// <param name="SearchLink">Where "all" leads.</param>
public sealed record SectionView(InboxSection Section, IReadOnlyList<MessageRow> Rows, int Total, string SearchLink);

/// <summary>rc.22 (item 55, mockups 20-21): the Inbox split into sections, each a saved search, off by default.</summary>
public sealed partial class MailboxService
{
    /// <summary>The most sections.</summary>
    public const int MaxSections = 6;

    /// <summary>A person's sections setting.</summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The setting; off with no sections when never set.</returns>
    public async Task<InboxSections> SectionsOfAsync(Guid mailboxId, CancellationToken ct = default) =>
        await this.ReadDocumentAsync<InboxSections>(mailboxId, InboxSections.Kind, ct).ConfigureAwait(false) ?? new InboxSections();

    /// <summary>Turn sections on or off; turning on for the first time gives the two starting sections.</summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="on">On or off.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task SetSectionsOnAsync(Guid mailboxId, bool on, CancellationToken ct = default)
    {
        InboxSections s = await this.SectionsOfAsync(mailboxId, ct).ConfigureAwait(false);
        s.On = on;
        if (on && s.List.Count == 0)
        {
            s.List = InboxSections.Defaults();
        }
        await this.WriteDocumentAsync(mailboxId, InboxSections.Kind, s, ct).ConfigureAwait(false);
    }

    /// <summary>Add or change a section (by its position; -1 adds). Returns the error, or null.</summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="index">The position, or -1 to add.</param>
    /// <param name="name">Its name.</param>
    /// <param name="query">Its search.</param>
    /// <param name="rows">Rows shown.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> SaveSectionAsync(Guid mailboxId, int index, string name, string query, int rows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(query);
        string n = Clip(name.Trim(), 40);
        string q = Clip(query.Trim(), 200);
        if (n.Length == 0 || q.Length == 0)
        {
            return "Give the section a name and a search.";
        }
        InboxSections s = await this.SectionsOfAsync(mailboxId, ct).ConfigureAwait(false);
        var section = new InboxSection { Name = n, Query = q, Rows = InboxSections.RowChoices.Contains(rows) ? rows : 5 };
        if (index >= 0 && index < s.List.Count)
        {
            s.List[index] = section;
        }
        else
        {
            if (s.List.Count >= MaxSections)
            {
                return $"Up to {MaxSections} sections.";
            }
            s.List.Add(section);
        }
        await this.WriteDocumentAsync(mailboxId, InboxSections.Kind, s, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Remove a section by its position.</summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="index">The position.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task RemoveSectionAsync(Guid mailboxId, int index, CancellationToken ct = default)
    {
        InboxSections s = await this.SectionsOfAsync(mailboxId, ct).ConfigureAwait(false);
        if (index >= 0 && index < s.List.Count)
        {
            s.List.RemoveAt(index);
            await this.WriteDocumentAsync(mailboxId, InboxSections.Kind, s, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The sections' rows, from the 300 most recent Inbox messages: is:unread, is:flagged,
    /// from:, has:attachment, newer:Nd and words in the subject or sender. Empty when sections are off.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>One view per section.</returns>
    public async Task<IReadOnlyList<SectionView>> SectionViewsAsync(Guid mailboxId, CancellationToken ct = default)
    {
        InboxSections s = await this.SectionsOfAsync(mailboxId, ct).ConfigureAwait(false);
        if (!s.On || s.List.Count == 0)
        {
            return Array.Empty<SectionView>();
        }
        FolderRow? inbox = await this.GetFolderAsync(mailboxId, FolderRow.Inbox, ct).ConfigureAwait(false);
        if (inbox is null)
        {
            return Array.Empty<SectionView>();
        }
        IReadOnlyList<MessageRow> recent = await this.store.ListMessagesAsync(mailboxId, inbox.Id, 300, 0, ct).ConfigureAwait(false);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var views = new List<SectionView>();
        foreach (InboxSection section in s.List)
        {
            List<MessageRow> matched = recent.Where(m => SectionMatches(section.Query, m, now)).ToList();
            views.Add(new SectionView(section, matched.Take(section.Rows).ToList(), matched.Count, "/search?q=" + Uri.EscapeDataString(section.Query) + "&from=INBOX"));
        }
        return views;
    }

    /// <summary>Whether a message matches a section's search.</summary>
    /// <param name="query">The search.</param>
    /// <param name="m">The message.</param>
    /// <param name="now">Now, for newer:Nd.</param>
    /// <returns>True when it matches.</returns>
    public static bool SectionMatches(string query, MessageRow m, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(m);
        string from = Anjal.Mime.EncodedWordDecoder.Decode(m.FromHeader) + " " + m.EnvelopeFrom;
        foreach (string token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = token.IndexOf(':', StringComparison.Ordinal);
            string key = colon > 0 ? token[..colon].ToLowerInvariant() : string.Empty;
            string value = colon > 0 ? token[(colon + 1)..] : token;
            bool ok = key switch
            {
                "is" => value.Equals("unread", StringComparison.OrdinalIgnoreCase) ? !m.Seen
                    : value.Equals("read", StringComparison.OrdinalIgnoreCase) ? m.Seen
                    : value.Equals("flagged", StringComparison.OrdinalIgnoreCase) || value.Equals("starred", StringComparison.OrdinalIgnoreCase) ? m.Flagged
                    : true,
                "from" => from.Contains(value, StringComparison.OrdinalIgnoreCase),
                "has" => m.HasAttachments,
                "newer" => value.EndsWith('d') && int.TryParse(value.TrimEnd('d'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) ? m.ReceivedAt >= now.AddDays(-days) : true,
                "subject" => m.Subject.Contains(value, StringComparison.OrdinalIgnoreCase),
                _ => m.Subject.Contains(value, StringComparison.OrdinalIgnoreCase) || from.Contains(value, StringComparison.OrdinalIgnoreCase) || m.Preview.Contains(value, StringComparison.OrdinalIgnoreCase),
            };
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }
}
