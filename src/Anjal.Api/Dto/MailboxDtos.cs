namespace Anjal.Api.Dto;

/// <summary>Request body for creating or updating a tenant.</summary>
public sealed class TenantRequest
{
    /// <summary>Unique slug: lowercase letters, digits and hyphens, 1-63 characters.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Human-readable name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>When false, no mail is delivered to the tenant's mailboxes.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Response body for a tenant.</summary>
public sealed class TenantResponse
{
    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The slug.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Human-readable name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Whether the tenant is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>When the tenant was created.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Request body for registering a domain to a tenant.</summary>
public sealed class TenantDomainRequest
{
    /// <summary>Slug of the owning tenant. Must already exist.</summary>
    public string TenantSlug { get; set; } = string.Empty;

    /// <summary>The domain (case-insensitive).</summary>
    public string Domain { get; set; } = string.Empty;
}

/// <summary>Response body for a tenant domain.</summary>
public sealed class TenantDomainResponse
{
    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The owning tenant's id.</summary>
    public System.Guid TenantId { get; set; }

    /// <summary>The owning tenant's slug.</summary>
    public string TenantSlug { get; set; } = string.Empty;

    /// <summary>The domain, lowercase.</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>Whether ownership is verified (always true for admin-API inserts in this release).</summary>
    public bool Verified { get; set; }

    /// <summary>When the domain was registered.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Request body for creating or updating a mailbox. The password is
/// plaintext and is hashed via <see cref="Anjal.Smtp.Pbkdf2Hasher"/>
/// before persisting. On update, an omitted or empty password keeps the
/// existing one.
/// </summary>
public sealed class MailboxRequest
{
    /// <summary>Slug of the owning tenant. The address domain must be registered to this tenant.</summary>
    public string TenantSlug { get; set; } = string.Empty;

    /// <summary>The full address <c>local@domain</c> (case-insensitive).</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>Plaintext password for submission auth. Empty on create means receive-only.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Display name for the From header.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>When false, delivery and authentication both fail.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Soft quota in bytes. Null or 0 means the default (2 GiB).</summary>
    public long? QuotaBytes { get; set; }
}

/// <summary>Response body for a mailbox. The password hash is never returned.</summary>
public sealed class MailboxResponse
{
    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The owning tenant's id.</summary>
    public System.Guid TenantId { get; set; }

    /// <summary>The full address <c>local@domain</c>.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>Local-part.</summary>
    public string LocalPart { get; set; } = string.Empty;

    /// <summary>Domain.</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>Display name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Whether the mailbox is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Whether the mailbox has submission credentials.</summary>
    public bool CanAuthenticate { get; set; }

    /// <summary>Soft quota in bytes.</summary>
    public long QuotaBytes { get; set; }

    /// <summary>Bytes currently stored.</summary>
    public long UsedBytes { get; set; }

    /// <summary>When the mailbox was created.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the mailbox was last updated.</summary>
    public System.DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Response body for a folder.</summary>
public sealed class FolderResponse
{
    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>Folder name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Number of messages in the folder.</summary>
    public long MessageCount { get; set; }

    /// <summary>When the folder was created.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Response body for one message's metadata (no body).</summary>
public sealed class MessageResponse
{
    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The owning mailbox's id.</summary>
    public System.Guid MailboxId { get; set; }

    /// <summary>The folder's id.</summary>
    public System.Guid FolderId { get; set; }

    /// <summary>SMTP envelope MAIL FROM.</summary>
    public string EnvelopeFrom { get; set; } = string.Empty;

    /// <summary>Message-ID header without angle brackets.</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>Raw From header.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>Raw To header.</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>Decoded Subject header.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Raw Date header.</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>Size in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Read flag.</summary>
    public bool Seen { get; set; }

    /// <summary>Flagged/starred.</summary>
    public bool Flagged { get; set; }

    /// <summary>Replied to.</summary>
    public bool Answered { get; set; }

    /// <summary>When the message was delivered.</summary>
    public System.DateTimeOffset ReceivedAt { get; set; }
}

/// <summary>Response body for a page of messages.</summary>
public sealed class MessagePageResponse
{
    /// <summary>The messages in this page, newest first.</summary>
    public System.Collections.Generic.IList<MessageResponse> Items { get; set; }
        = new System.Collections.Generic.List<MessageResponse>();

    /// <summary>Total messages matching the query (all pages).</summary>
    public long Total { get; set; }

    /// <summary>The offset this page started at.</summary>
    public int Offset { get; set; }

    /// <summary>The page size requested.</summary>
    public int Limit { get; set; }
}

/// <summary>Response body for a message's raw bytes.</summary>
public sealed class MessageRawResponse
{
    /// <summary>Identifier of the message.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The full RFC 5322 message, base64 encoded.</summary>
    public string RawBytesBase64 { get; set; } = string.Empty;
}
