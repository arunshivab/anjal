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

    /// <summary>Spam score assigned at delivery (0 when scoring was not run).</summary>
    public int SpamScore { get; set; }

    /// <summary>Time the message was delivered to the folder.</summary>
    public System.DateTimeOffset ReceivedAt { get; set; }
}
