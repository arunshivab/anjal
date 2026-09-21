namespace Anjal.Store;

/// <summary>
/// A tenant: one customer organisation hosted on an Anjal deployment.
/// Every mailbox, domain and message belongs to exactly one tenant. The
/// <see cref="Slug"/> doubles as the on-disk directory name under the
/// Maildir root, so it is restricted to lowercase letters, digits and
/// hyphens.
/// </summary>
public sealed class TenantRow
{
    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>
    /// Unique, URL-safe and filesystem-safe identifier (e.g. "imagiqa").
    /// Lowercase letters, digits and hyphens only; 1-63 characters.
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Human-readable name (e.g. "imagiQa Healthcare Services").</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>When false, no mail is delivered to any mailbox of this tenant.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Default spam threshold for a new tenant.</summary>
    public const int DefaultSpamThreshold = 5;

    /// <summary>
    /// Messages scoring at or above this land in Junk instead of INBOX.
    /// Scores are integers; see <c>Anjal.Spam.SpamScorer</c> for the rules.
    /// </summary>
    public int SpamThreshold { get; set; } = DefaultSpamThreshold;

    /// <summary>When the tenant was created.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }
}

/// <summary>What a <see cref="SenderRuleRow"/> does to matching mail.</summary>
public enum SenderRuleAction
{
    /// <summary>Always deliver to INBOX regardless of score.</summary>
    Allow = 0,

    /// <summary>Always deliver to Junk regardless of score.</summary>
    Block = 1,
}

/// <summary>
/// A per-tenant sender allow/block rule. The pattern is either a full
/// address (<c>alice@example.com</c>) or a domain with a leading "@"
/// (<c>@example.com</c>, which also matches subdomains). Patterns are
/// matched case-insensitively against the envelope MAIL FROM and the
/// From header address. A block rule wins over an allow rule when both
/// match; an exact-address rule wins over a domain rule.
/// </summary>
public sealed class SenderRuleRow
{
    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The owning tenant.</summary>
    public System.Guid TenantId { get; set; }

    /// <summary>Address or "@domain" pattern, lowercase.</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>Allow or block.</summary>
    public SenderRuleAction Action { get; set; }

    /// <summary>When the rule was created.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A domain owned by a tenant. Mail addressed to <c>anything@domain</c>
/// is looked up against the tenant's mailboxes. A domain belongs to at
/// most one tenant across the whole deployment.
/// </summary>
public sealed class TenantDomainRow
{
    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The owning tenant.</summary>
    public System.Guid TenantId { get; set; }

    /// <summary>The domain, lowercase (e.g. "anjal.co.in").</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>
    /// Whether ownership has been verified. Admin-API inserts are treated
    /// as verified; a self-service verification flow (DNS TXT challenge)
    /// is planned for a later release and will set this to false until
    /// the challenge passes.
    /// </summary>
    public bool Verified { get; set; } = true;

    /// <summary>When the domain was registered.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A mailbox: a receiving identity <c>local_part@domain</c> belonging to
/// a tenant. The mailbox also carries submission credentials so that the
/// same identity can authenticate on the submission port and send as
/// itself (Dovecot/Postfix "virtual user" pattern).
/// </summary>
public sealed class MailboxRow
{
    /// <summary>Two gibibytes - the default per-mailbox quota.</summary>
    public const long DefaultQuotaBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The owning tenant.</summary>
    public System.Guid TenantId { get; set; }

    /// <summary>Local-part of the address, lowercase (left of "@", no "+tag").</summary>
    public string LocalPart { get; set; } = string.Empty;

    /// <summary>Domain of the address, lowercase. Must be a domain of the tenant.</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>The full address <c>local_part@domain</c>.</summary>
    public string Address => this.LocalPart + "@" + this.Domain;

    /// <summary>
    /// PBKDF2 password hash in <c>Anjal.Smtp.Pbkdf2Hasher</c> format,
    /// used for submission authentication. Empty means the mailbox is
    /// receive-only and cannot authenticate.
    /// </summary>
    public string PasswordPbkdf2 { get; set; } = string.Empty;

    /// <summary>Display name for the From header (e.g. "Arun Shiva B").</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Default webmail theme.</summary>
    public const string DefaultTheme = "paper";

    /// <summary>Webmail theme for this mailbox: paper, ink, postcard or midnight. Stored per mailbox, not per browser.</summary>
    public string Theme { get; set; } = DefaultTheme;

    /// <summary>When false, delivery and authentication both fail.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Soft quota in bytes. Exceeding it is logged; hard enforcement is a later release.</summary>
    public long QuotaBytes { get; set; } = DefaultQuotaBytes;

    /// <summary>Bytes currently stored in this mailbox, maintained by the store on each delivery.</summary>
    public long UsedBytes { get; set; }

    /// <summary>When the mailbox was created.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the mailbox was created or last updated.</summary>
    public System.DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A folder inside a mailbox. INBOX maps to the Maildir root; every other
/// folder maps to a <c>.Name</c> subdirectory (Maildir++ convention).
/// </summary>
public sealed class FolderRow
{
    /// <summary>The name of the inbox folder.</summary>
    public const string Inbox = "INBOX";

    /// <summary>The longest folder name accepted.</summary>
    public const int MaxNameLength = 64;

    /// <summary>
    /// Whether a name is acceptable for a folder: 1-64 printable ASCII
    /// characters, no path separators, not starting with a dot. The store
    /// refuses anything else, so no caller can create a folder whose name
    /// would mean something to the filesystem.
    /// </summary>
    /// <param name="name">Candidate name.</param>
    public static bool IsValidName(string name)
    {
        System.ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || name.Length > MaxNameLength || name[0] == '.' || name.Trim().Length != name.Length)
        {
            return false;
        }
        foreach (char c in name)
        {
            if (c < ' ' || c > '~' || c == '/' || c == '\\')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The owning mailbox.</summary>
    public System.Guid MailboxId { get; set; }

    /// <summary>Folder name, case-sensitive as shown to users (INBOX, Sent, Drafts, Trash, ...).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>When the folder was created.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The Maildir++ directory name for a folder: empty for INBOX (the
    /// Maildir root), otherwise <c>.Name</c>.
    /// </summary>
    public static string MaildirNameFor(string name)
    {
        System.ArgumentNullException.ThrowIfNull(name);
        return string.Equals(name, Inbox, System.StringComparison.Ordinal) ? string.Empty : "." + name;
    }
}

/// <summary>
/// Metadata for one message stored in a mailbox folder. The message body
/// lives only in the Maildir file named by <see cref="MaildirFile"/>; the
/// store row exists for listing, searching and flagging without touching
/// the filesystem.
/// </summary>
public sealed class MessageRow
{
    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The owning mailbox.</summary>
    public System.Guid MailboxId { get; set; }

    /// <summary>The folder the message is in.</summary>
    public System.Guid FolderId { get; set; }

    /// <summary>
    /// Path of the Maildir file relative to the folder's Maildir directory,
    /// e.g. <c>new/1726560000.M123456P4242Q7.host</c>.
    /// </summary>
    public string MaildirFile { get; set; } = string.Empty;

    /// <summary>SMTP envelope MAIL FROM.</summary>
    public string EnvelopeFrom { get; set; } = string.Empty;

    /// <summary>The Message-ID header value with angle brackets stripped, or empty.</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>The raw From header value.</summary>
    public string FromHeader { get; set; } = string.Empty;

    /// <summary>The raw To header value.</summary>
    public string ToHeader { get; set; } = string.Empty;

    /// <summary>The Subject header, decoded if RFC 2047 encoded.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>The raw Date header value, or empty.</summary>
    public string DateHeader { get; set; } = string.Empty;

    /// <summary>Size of the Maildir file in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Maildir "S" flag - the message has been read.</summary>
    public bool Seen { get; set; }

    /// <summary>Maildir "F" flag - the message is flagged/starred.</summary>
    public bool Flagged { get; set; }

    /// <summary>Maildir "R" flag - the message has been replied to.</summary>
    public bool Answered { get; set; }

    /// <summary>The category this message carries, or null.</summary>
    public System.Guid? CategoryId { get; set; }

    /// <summary>True when the message has at least one attachment part.</summary>
    public bool HasAttachments { get; set; }

    /// <summary>Spam score assigned at delivery (0 when scoring was not run).</summary>
    public int SpamScore { get; set; }

    /// <summary>Time the message was delivered to the folder.</summary>
    public System.DateTimeOffset ReceivedAt { get; set; }
}

/// <summary>
/// A named label a mailbox can put on a message. Categories exist at two
/// levels: a tenant's defaults, shared by every mailbox in it and the only
/// ones an institution can report across, and a mailbox's own additions,
/// private to that mailbox.
/// <para>
/// Colour comes from <see cref="Slot"/>, one of eight validated slots.
/// The eight are a per-mailbox budget: the tenant's defaults take slots in
/// order, a mailbox's own take what remains, and anything past the eighth
/// keeps <see cref="NoSlot"/> and is told apart by name alone. Slots are
/// stored, never derived from the name and never recomputed when a
/// category is deleted, because recomputing repaints every chart.
/// </para>
/// </summary>
public sealed class CategoryRow
{
    /// <summary>Slot value meaning "no colour left"; rendered in ink-subtle.</summary>
    public const int NoSlot = 0;

    /// <summary>The highest colour slot the design system defines.</summary>
    public const int MaxSlot = 8;

    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The tenant this category belongs to.</summary>
    public System.Guid TenantId { get; set; }

    /// <summary>
    /// The mailbox that owns it, or null for a tenant default shared by
    /// every mailbox in the tenant.
    /// </summary>
    public System.Guid? MailboxId { get; set; }

    /// <summary>Display name, unique within its scope.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Colour slot 1-8, or <see cref="NoSlot"/>.</summary>
    public int Slot { get; set; }

    /// <summary>When it was created.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }

    /// <summary>True when this is a tenant default rather than a mailbox's own.</summary>
    public bool IsShared => this.MailboxId is null;
}

/// <summary>
/// "File mail from this sender under this category." Written when the
/// reader assigns a category and asks for future mail to follow, and
/// applied at delivery. Owned by one mailbox: categorising is a personal
/// act even when the category is shared.
/// </summary>
public sealed class CategoryRuleRow
{
    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The mailbox whose mail this rule files.</summary>
    public System.Guid MailboxId { get; set; }

    /// <summary>
    /// Sender pattern: a full address (<c>lab@example.com</c>) or a domain
    /// (<c>@example.com</c>), matched the same way sender rules are.
    /// </summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>The category to apply.</summary>
    public System.Guid CategoryId { get; set; }

    /// <summary>When it was created.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One day's message counts, for the dashboard's line chart.</summary>
public sealed class DailyCount
{
    /// <summary>The day (UTC date at midnight).</summary>
    public System.DateTimeOffset Day { get; set; }

    /// <summary>Messages received that day.</summary>
    public long Received { get; set; }

    /// <summary>Messages sent that day.</summary>
    public long Sent { get; set; }
}

/// <summary>A label and a count, for the dashboard's bar charts.</summary>
public sealed class NamedCount
{
    /// <summary>What is being counted: a folder, a category, a sender.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>How many.</summary>
    public long Count { get; set; }

    /// <summary>Colour slot when the name is a category; otherwise <see cref="CategoryRow.NoSlot"/>.</summary>
    public int Slot { get; set; }
}

/// <summary>What a mailbox did over a period. Everything the dashboard shows.</summary>
public sealed class MailboxActivity
{
    /// <summary>Start of the period (inclusive, UTC).</summary>
    public System.DateTimeOffset From { get; set; }

    /// <summary>End of the period (exclusive, UTC).</summary>
    public System.DateTimeOffset To { get; set; }

    /// <summary>Messages delivered to a folder other than Junk.</summary>
    public long Delivered { get; set; }

    /// <summary>Messages filed in Junk.</summary>
    public long Junked { get; set; }

    /// <summary>Junk messages the reader moved back to INBOX.</summary>
    public long RecoveredFromJunk { get; set; }

    /// <summary>Messages sent.</summary>
    public long Sent { get; set; }

    /// <summary>Messages received carrying at least one attachment.</summary>
    public long ReceivedWithAttachments { get; set; }

    /// <summary>Messages sent carrying at least one attachment.</summary>
    public long SentWithAttachments { get; set; }

    /// <summary>Received messages per folder.</summary>
    public System.Collections.Generic.IReadOnlyList<NamedCount> ByFolder { get; set; } = System.Array.Empty<NamedCount>();

    /// <summary>Received messages per category, including "Uncategorised".</summary>
    public System.Collections.Generic.IReadOnlyList<NamedCount> ByCategory { get; set; } = System.Array.Empty<NamedCount>();

    /// <summary>The five people who wrote most.</summary>
    public System.Collections.Generic.IReadOnlyList<NamedCount> TopSenders { get; set; } = System.Array.Empty<NamedCount>();

    /// <summary>Sent and received per day, oldest first.</summary>
    public System.Collections.Generic.IReadOnlyList<DailyCount> ByDay { get; set; } = System.Array.Empty<DailyCount>();
}
