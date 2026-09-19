namespace Anjal.Store;

/// <summary>
/// Persistence interface for multi-tenant mailbox storage: tenants, their
/// domains, mailboxes, folders and per-message metadata. Message bodies
/// are NOT stored here - they live in Maildir files on disk; this
/// interface holds only the index. Both
/// <see cref="InMemoryMessageStore"/> and <see cref="PostgresMessageStore"/>
/// implement it alongside <see cref="IMessageStore"/>.
/// </summary>
public interface IMailboxStore
{
    // -------- Tenants --------

    /// <summary>
    /// Create or update a tenant keyed by <see cref="TenantRow.Slug"/>.
    /// Returns the saved row with <see cref="TenantRow.Id"/> populated.
    /// </summary>
    System.Threading.Tasks.Task<TenantRow> UpsertTenantAsync(TenantRow tenant, System.Threading.CancellationToken ct = default);

    /// <summary>Look up a tenant by slug (case-insensitive). Null if none.</summary>
    System.Threading.Tasks.Task<TenantRow?> GetTenantAsync(string slug, System.Threading.CancellationToken ct = default);

    /// <summary>Look up a tenant by id. Null if none.</summary>
    System.Threading.Tasks.Task<TenantRow?> GetTenantByIdAsync(System.Guid id, System.Threading.CancellationToken ct = default);

    /// <summary>List all tenants ordered by slug.</summary>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<TenantRow>> ListTenantsAsync(System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Remove a tenant and, by cascade, its domains, mailboxes, folders
    /// and message rows. Maildir files on disk are NOT removed.
    /// </summary>
    /// <returns><see langword="true"/> if a tenant was removed.</returns>
    System.Threading.Tasks.Task<bool> DeleteTenantAsync(string slug, System.Threading.CancellationToken ct = default);

    // -------- Tenant domains --------

    /// <summary>
    /// Register a domain for a tenant. The domain is unique across the
    /// deployment; upserting an existing domain moves it to the given
    /// tenant and updates the verified flag.
    /// </summary>
    System.Threading.Tasks.Task<TenantDomainRow> UpsertTenantDomainAsync(TenantDomainRow domain, System.Threading.CancellationToken ct = default);

    /// <summary>Look up a domain row (case-insensitive). Null if none.</summary>
    System.Threading.Tasks.Task<TenantDomainRow?> GetTenantDomainAsync(string domain, System.Threading.CancellationToken ct = default);

    /// <summary>List domains, optionally restricted to one tenant. Ordered by domain.</summary>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<TenantDomainRow>> ListTenantDomainsAsync(System.Guid? tenantId = null, System.Threading.CancellationToken ct = default);

    /// <summary>Remove a domain. Returns <see langword="true"/> if a row was removed.</summary>
    System.Threading.Tasks.Task<bool> DeleteTenantDomainAsync(string domain, System.Threading.CancellationToken ct = default);

    // -------- Mailboxes --------

    /// <summary>
    /// Create or update a mailbox keyed by (local-part, domain). On update
    /// the password hash is replaced only when the supplied hash is
    /// non-empty; <see cref="MailboxRow.UsedBytes"/> is never overwritten.
    /// </summary>
    System.Threading.Tasks.Task<MailboxRow> UpsertMailboxAsync(MailboxRow mailbox, System.Threading.CancellationToken ct = default);

    /// <summary>Look up a mailbox by local-part and domain (case-insensitive). Null if none.</summary>
    System.Threading.Tasks.Task<MailboxRow?> GetMailboxAsync(string localPart, string domain, System.Threading.CancellationToken ct = default);

    /// <summary>Look up a mailbox by id. Null if none.</summary>
    System.Threading.Tasks.Task<MailboxRow?> GetMailboxByIdAsync(System.Guid id, System.Threading.CancellationToken ct = default);

    /// <summary>List mailboxes, optionally restricted to one tenant. Ordered by domain then local-part.</summary>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<MailboxRow>> ListMailboxesAsync(System.Guid? tenantId = null, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Remove a mailbox and, by cascade, its folders and message rows.
    /// Maildir files on disk are NOT removed.
    /// </summary>
    System.Threading.Tasks.Task<bool> DeleteMailboxAsync(string localPart, string domain, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Adjust <see cref="MailboxRow.UsedBytes"/> by a signed delta and
    /// return the new total. Returns <see langword="null"/> if no such mailbox.
    /// </summary>
    System.Threading.Tasks.Task<long?> AddMailboxUsageAsync(System.Guid mailboxId, long deltaBytes, System.Threading.CancellationToken ct = default);

    // -------- Folders --------

    /// <summary>
    /// Ensure a folder exists for a mailbox. Idempotent by (mailbox, name);
    /// returns the existing or newly created row.
    /// </summary>
    System.Threading.Tasks.Task<FolderRow> EnsureFolderAsync(System.Guid mailboxId, string name, System.Threading.CancellationToken ct = default);

    /// <summary>List a mailbox's folders ordered by name, INBOX first.</summary>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<FolderRow>> ListFoldersAsync(System.Guid mailboxId, System.Threading.CancellationToken ct = default);

    // -------- Messages --------

    /// <summary>
    /// Record a delivered message. Returns the saved row with
    /// <see cref="MessageRow.Id"/> and <see cref="MessageRow.ReceivedAt"/> populated.
    /// </summary>
    System.Threading.Tasks.Task<MessageRow> SaveMessageAsync(MessageRow message, System.Threading.CancellationToken ct = default);

    /// <summary>Fetch one message row by id. Null if none.</summary>
    System.Threading.Tasks.Task<MessageRow?> GetMessageByIdAsync(System.Guid id, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Page through a folder's messages, newest first.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="folderId">The folder, or null for all folders of the mailbox.</param>
    /// <param name="limit">Maximum rows to return.</param>
    /// <param name="offset">Rows to skip.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<MessageRow>> ListMessagesAsync(System.Guid mailboxId, System.Guid? folderId, int limit, int offset, System.Threading.CancellationToken ct = default);

    /// <summary>Count messages in a folder (or the whole mailbox when <paramref name="folderId"/> is null).</summary>
    System.Threading.Tasks.Task<long> CountMessagesAsync(System.Guid mailboxId, System.Guid? folderId, System.Threading.CancellationToken ct = default);
}
