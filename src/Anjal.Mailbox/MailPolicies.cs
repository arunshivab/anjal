using System.Text.Json;

namespace Anjal.Mailbox;

/// <summary>
/// What an organisation lets its people do with their mail (rc.22, owner's decisions of 18 Oct
/// 2026), kept as the organisation's "mailrules" document and read at delivery by every process.
/// </summary>
public sealed class OrgMailPolicy
{
    /// <summary>The tenant document kind.</summary>
    public const string Kind = "mailrules";

    /// <summary>
    /// Whether rules may forward a copy to an address outside the organisation (item 29). Forwarding
    /// inside the organisation is always allowed; every forward is logged. Individuals (an
    /// "individuals" tenant) may forward anywhere.
    /// </summary>
    public bool ForwardingOutside { get; set; }

    /// <summary>Whether people may choose the name on their mails (item 37); off means first name + last name.</summary>
    public bool Nicknames { get; set; }

    /// <summary>Whether out-of-office replies go to senders outside the organisation (item 30).</summary>
    public bool AwayOutside { get; set; } = true;

    /// <summary>Read an organisation's policy; Anjal's defaults when it has set none.</summary>
    /// <param name="store">The store.</param>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The policy.</returns>
    public static async Task<OrgMailPolicy> ReadAsync(Anjal.Store.IMailboxStore store, System.Guid tenantId, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        return MailDocuments.Parse<OrgMailPolicy>(await store.GetTenantDocumentAsync(tenantId, Kind, ct).ConfigureAwait(false)) ?? new OrgMailPolicy();
    }
}

/// <summary>An extra address that delivers to a person's own mailbox (rc.22, item 41).</summary>
public sealed class AliasEntry
{
    /// <summary>The address, lower case, on one of the organisation's domains.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>The mailbox it delivers to.</summary>
    public System.Guid MailboxId { get; set; }

    /// <summary>Whether the person may also send from it.</summary>
    public bool CanSend { get; set; } = true;
}

/// <summary>One address that reaches several people, each in their own Inbox (rc.22, item 41).</summary>
public sealed class GroupEntry
{
    /// <summary>The address, lower case.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>The members' mailboxes.</summary>
    public List<System.Guid> Members { get; set; } = new();

    /// <summary>True when only the organisation's own people may write to it; mail from outside is refused.</summary>
    public bool OrgOnly { get; set; }
}

/// <summary>
/// An organisation's extra addresses and group addresses (rc.22, item 41), kept as its "addresses"
/// document. A shared mailbox is different: one Inbox several people open. A group address delivers
/// a copy to each member's own Inbox.
/// </summary>
public sealed class AddressBook
{
    /// <summary>The tenant document kind.</summary>
    public const string Kind = "addresses";

    /// <summary>At most this many members in a group.</summary>
    public const int MaxMembers = 200;

    /// <summary>The extra addresses.</summary>
    public List<AliasEntry> Aliases { get; set; } = new();

    /// <summary>The group addresses.</summary>
    public List<GroupEntry> Groups { get; set; } = new();

    /// <summary>Read an organisation's address book (empty when it has none).</summary>
    /// <param name="store">The store.</param>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The book.</returns>
    public static async Task<AddressBook> ReadAsync(Anjal.Store.IMailboxStore store, System.Guid tenantId, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        return MailDocuments.Parse<AddressBook>(await store.GetTenantDocumentAsync(tenantId, Kind, ct).ConfigureAwait(false)) ?? new AddressBook();
    }

    /// <summary>Save an organisation's address book.</summary>
    /// <param name="store">The store.</param>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="book">The book.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public static Task WriteAsync(Anjal.Store.IMailboxStore store, System.Guid tenantId, AddressBook book, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(book);
        return store.SetTenantDocumentAsync(tenantId, Kind, MailDocuments.Write(book), ct);
    }

    /// <summary>The alias at an address, or null.</summary>
    /// <param name="address">The address, in any case.</param>
    /// <returns>The alias.</returns>
    public AliasEntry? AliasAt(string address)
    {
        System.ArgumentNullException.ThrowIfNull(address);
        return this.Aliases.Find(a => string.Equals(a.Address, address, System.StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The group at an address, or null.</summary>
    /// <param name="address">The address, in any case.</param>
    /// <returns>The group.</returns>
    public GroupEntry? GroupAt(string address)
    {
        System.ArgumentNullException.ThrowIfNull(address);
        return this.Groups.Find(g => string.Equals(g.Address, address, System.StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// A person's out-of-office reply (rc.22, item 30), kept as their "away" document. Each sender is
/// answered once while it is on; mail from applications, lists and no-reply addresses never is.
/// </summary>
public sealed class AwayReply
{
    /// <summary>The mailbox document kind.</summary>
    public const string Kind = "away";

    /// <summary>The choices for who is answered: everyone, only the organisation, only contacts.</summary>
    public static readonly IReadOnlyList<string> ReplyToChoices = new[] { "everyone", "org", "contacts" };

    /// <summary>Whether the reply is on.</summary>
    public bool On { get; set; }

    /// <summary>From when (null: from now).</summary>
    public System.DateTimeOffset? From { get; set; }

    /// <summary>Until when (null: until it is turned off); it turns itself off then.</summary>
    public System.DateTimeOffset? Until { get; set; }

    /// <summary>The reply's subject; empty gives "Automatic reply: " and the original subject.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>The reply's text.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>"everyone", "org" (only people in the organisation) or "contacts" (only saved contacts).</summary>
    public string ReplyTo { get; set; } = "everyone";

    /// <summary>Addresses or words never answered, comma separated (for example "newsletters, no-reply@").</summary>
    public string Skip { get; set; } = string.Empty;

    /// <summary>Who was answered and when, so each sender hears once.</summary>
    public Dictionary<string, System.DateTimeOffset> Replied { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the reply is on at a time: switched on, after From, before Until.</summary>
    /// <param name="now">The time.</param>
    /// <returns>True when a reply would be sent.</returns>
    public bool IsActive(System.DateTimeOffset now) =>
        this.On && (this.From is null || this.From <= now) && (this.Until is null || this.Until > now);

    /// <summary>Read a mailbox's away reply (off when it has none).</summary>
    /// <param name="store">The store.</param>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The reply.</returns>
    public static async Task<AwayReply> ReadAsync(Anjal.Store.IMailboxStore store, System.Guid mailboxId, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        AwayReply away = MailDocuments.Parse<AwayReply>(await store.GetMailboxDocumentAsync(mailboxId, Kind, ct).ConfigureAwait(false)) ?? new AwayReply();
        away.Replied = new Dictionary<string, System.DateTimeOffset>(away.Replied, System.StringComparer.OrdinalIgnoreCase);
        return away;
    }

    /// <summary>Save a mailbox's away reply.</summary>
    /// <param name="store">The store.</param>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="away">The reply.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public static Task WriteAsync(Anjal.Store.IMailboxStore store, System.Guid mailboxId, AwayReply away, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(away);
        return store.SetMailboxDocumentAsync(mailboxId, Kind, MailDocuments.Write(away), ct);
    }

    /// <summary>
    /// Whether a sender is never answered: no address, the mailbox itself, a list or automatic
    /// message, a no-reply or bounce address, or a word from <see cref="Skip"/>.
    /// </summary>
    /// <param name="sender">The sender's address.</param>
    /// <param name="headers">The message's headers.</param>
    /// <returns>True when no reply goes.</returns>
    public bool Skips(string sender, Anjal.Mime.HeaderCollection headers)
    {
        System.ArgumentNullException.ThrowIfNull(sender);
        System.ArgumentNullException.ThrowIfNull(headers);
        string s = sender.Trim().ToLowerInvariant();
        if (s.Length == 0 || MailboxSink.IsAutomaticMail(headers))
        {
            return true;
        }
        string local = s.Split('@')[0];
        foreach (string word in new[] { "no-reply", "noreply", "donotreply", "do-not-reply", "mailer-daemon", "postmaster", "bounce", "notification", "newsletter" })
        {
            if (local.StartsWith(word, System.StringComparison.Ordinal) || local.EndsWith(word, System.StringComparison.Ordinal))
            {
                return true;
            }
        }
        foreach (string word in this.Skip.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries))
        {
            if (word.Length > 0 && s.Contains(word.ToLowerInvariant(), System.StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// What the virus scanner has done since it was switched on (rc.22, item 39), kept as the
/// service record "virus" and shown in the Anjal console.
/// </summary>
public sealed class VirusStats
{
    /// <summary>The service record kind.</summary>
    public const string Kind = "virus";

    /// <summary>Files scanned.</summary>
    public long Scanned { get; set; }

    /// <summary>Viruses removed (the mail delivered without the file).</summary>
    public long Removed { get; set; }

    /// <summary>Programs and scripts refused.</summary>
    public long Refused { get; set; }

    /// <summary>Disk images sent to Junk.</summary>
    public long Junked { get; set; }

    /// <summary>Mails held while the scanner was down.</summary>
    public long Held { get; set; }

    /// <summary>When the scanner last answered.</summary>
    public System.DateTimeOffset? LastSeen { get; set; }

    /// <summary>When the scanner was last found down.</summary>
    public System.DateTimeOffset? LastDown { get; set; }

    /// <summary>The scanner's version line, as it last reported it.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Since when the counts run.</summary>
    public System.DateTimeOffset Since { get; set; } = System.DateTimeOffset.UtcNow;

    /// <summary>Read the record (fresh when there is none or the store keeps no records).</summary>
    /// <param name="store">The store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The record.</returns>
    public static async Task<VirusStats> ReadAsync(Anjal.Store.IMailboxStore store, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        if (store is not Anjal.Store.IMessageStore records)
        {
            return new VirusStats();
        }
        return MailDocuments.Parse<VirusStats>(await records.GetServiceRecordAsync(Kind, ct).ConfigureAwait(false)) ?? new VirusStats();
    }

    /// <summary>Change the record and save it.</summary>
    /// <param name="store">The store.</param>
    /// <param name="change">What changes.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task UpdateAsync(Anjal.Store.IMailboxStore store, System.Action<VirusStats> change, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(change);
        if (store is not Anjal.Store.IMessageStore records)
        {
            return;
        }
        VirusStats stats = await ReadAsync(store, ct).ConfigureAwait(false);
        change(stats);
        await records.SetServiceRecordAsync(Kind, MailDocuments.Write(stats), ct).ConfigureAwait(false);
    }
}

/// <summary>
/// What kind of tenant an organisation record is (rc.22): an organisation, or the "individuals"
/// tenant that holds people with their own address on Anjal's shared domain (anjalmail.com),
/// each on their own, with no organisation around them. Kept as the tenant's "kind" document,
/// written by the Anjal console when the tenant is made.
/// </summary>
public sealed class TenantKind
{
    /// <summary>The tenant document kind.</summary>
    public const string Kind = "kind";

    /// <summary>True for the individuals tenant.</summary>
    public bool Individuals { get; set; }

    /// <summary>Whether a tenant is the individuals tenant.</summary>
    /// <param name="store">The store.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True for individuals.</returns>
    public static async Task<bool> IsIndividualsAsync(Anjal.Store.IMailboxStore store, System.Guid tenantId, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        return (MailDocuments.Parse<TenantKind>(await store.GetTenantDocumentAsync(tenantId, Kind, ct).ConfigureAwait(false)) ?? new TenantKind()).Individuals;
    }

    /// <summary>Record a tenant's kind.</summary>
    /// <param name="store">The store.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="individuals">True for the individuals tenant.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public static Task WriteAsync(Anjal.Store.IMailboxStore store, System.Guid tenantId, bool individuals, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        return store.SetTenantDocumentAsync(tenantId, Kind, MailDocuments.Write(new TenantKind { Individuals = individuals }), ct);
    }
}

/// <summary>Reading and writing the JSON documents the mail policies are kept in.</summary>
public static class MailDocuments
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = false };

    /// <summary>Read a document; an empty or broken one gives null.</summary>
    /// <typeparam name="T">Its shape.</typeparam>
    /// <param name="json">The document.</param>
    /// <returns>The value, or null.</returns>
    public static T? Parse<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Write a document.</summary>
    /// <typeparam name="T">Its shape.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The JSON.</returns>
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Json);
}
