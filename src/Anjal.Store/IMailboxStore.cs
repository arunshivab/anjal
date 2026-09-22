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

    /// <summary>
    /// Update a message's flags and, optionally, the Maildir file name that
    /// now carries them (Maildir encodes flags in the file name, so a flag
    /// change on disk is a rename). Returns the updated row, or
    /// <see langword="null"/> if no such message.
    /// </summary>
    /// <param name="id">The message.</param>
    /// <param name="seen">New seen flag.</param>
    /// <param name="flagged">New flagged flag.</param>
    /// <param name="answered">New answered flag.</param>
    /// <param name="maildirFile">New relative file path, or null to leave unchanged.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<MessageRow?> SetMessageFlagsAsync(System.Guid id, bool seen, bool flagged, bool answered, string? maildirFile, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Move a message to another folder of the same mailbox, recording the
    /// new Maildir file path. Returns the updated row, or
    /// <see langword="null"/> if no such message.
    /// </summary>
    /// <param name="id">The message.</param>
    /// <param name="folderId">Destination folder.</param>
    /// <param name="maildirFile">New relative file path within the destination folder.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<MessageRow?> MoveMessageAsync(System.Guid id, System.Guid folderId, string maildirFile, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Remove a message's index row. Returns <see langword="true"/> if a row
    /// was removed. The caller is responsible for the Maildir file.
    /// </summary>
    System.Threading.Tasks.Task<bool> DeleteMessageAsync(System.Guid id, System.Threading.CancellationToken ct = default);

    /// <summary>Count unread (not seen) messages in a folder, or the whole mailbox when <paramref name="folderId"/> is null.</summary>
    System.Threading.Tasks.Task<long> CountUnreadAsync(System.Guid mailboxId, System.Guid? folderId, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Case-insensitive substring search over subject, From, To and envelope
    /// sender, newest first. <paramref name="folderId"/> null searches every folder.
    /// </summary>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<MessageRow>> SearchMessagesAsync(System.Guid mailboxId, System.Guid? folderId, string query, int limit, int offset, System.Threading.CancellationToken ct = default);

    /// <summary>Count of <see cref="SearchMessagesAsync"/> matches.</summary>
    System.Threading.Tasks.Task<long> CountSearchAsync(System.Guid mailboxId, System.Guid? folderId, string query, System.Threading.CancellationToken ct = default);

    // -------- Categories --------

    /// <summary>
    /// Create or rename a category. A row with <see cref="CategoryRow.MailboxId"/>
    /// null is a tenant default; otherwise it belongs to that mailbox.
    /// The slot is assigned by the caller and never recomputed.
    /// </summary>
    System.Threading.Tasks.Task<CategoryRow> UpsertCategoryAsync(CategoryRow category, System.Threading.CancellationToken ct = default);

    /// <summary>Tenant defaults plus one mailbox's own, shared first then by slot.</summary>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<CategoryRow>> ListCategoriesAsync(System.Guid tenantId, System.Guid? mailboxId, System.Threading.CancellationToken ct = default);

    /// <summary>One category by id, or null.</summary>
    System.Threading.Tasks.Task<CategoryRow?> GetCategoryAsync(System.Guid id, System.Threading.CancellationToken ct = default);

    /// <summary>Delete a category and clear it from any message carrying it.</summary>
    System.Threading.Tasks.Task<bool> DeleteCategoryAsync(System.Guid id, System.Threading.CancellationToken ct = default);

    /// <summary>Put a category on a message, or clear it with null.</summary>
    System.Threading.Tasks.Task<MessageRow?> SetMessageCategoryAsync(System.Guid mailboxId, System.Guid messageId, System.Guid? categoryId, System.Threading.CancellationToken ct = default);

    /// <summary>Create or replace a sender-to-category rule for a mailbox.</summary>
    System.Threading.Tasks.Task<CategoryRuleRow> UpsertCategoryRuleAsync(CategoryRuleRow rule, System.Threading.CancellationToken ct = default);

    /// <summary>A mailbox's sender-to-category rules.</summary>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<CategoryRuleRow>> ListCategoryRulesAsync(System.Guid mailboxId, System.Threading.CancellationToken ct = default);

    /// <summary>Remove a sender-to-category rule.</summary>
    System.Threading.Tasks.Task<bool> DeleteCategoryRuleAsync(System.Guid id, System.Threading.CancellationToken ct = default);

    // -------- Dashboard aggregates --------

    /// <summary>
    /// Everything the dashboard shows for one mailbox over a period,
    /// computed in the store rather than by reading every message.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="periodStart">Start, inclusive.</param>
    /// <param name="periodEnd">End, exclusive.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<MailboxActivity> GetActivityAsync(System.Guid mailboxId, System.DateTimeOffset periodStart, System.DateTimeOffset periodEnd, System.Threading.CancellationToken ct = default);

    // -------- Sender rules --------

    /// <summary>
    /// Create or replace a sender rule keyed by (tenant, pattern). Returns
    /// the saved row.
    /// </summary>
    System.Threading.Tasks.Task<SenderRuleRow> UpsertSenderRuleAsync(SenderRuleRow rule, System.Threading.CancellationToken ct = default);

    /// <summary>List a tenant's sender rules ordered by pattern.</summary>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<SenderRuleRow>> ListSenderRulesAsync(System.Guid tenantId, System.Threading.CancellationToken ct = default);

    /// <summary>Remove a sender rule. Returns <see langword="true"/> if a row was removed.</summary>
    System.Threading.Tasks.Task<bool> DeleteSenderRuleAsync(System.Guid tenantId, string pattern, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// A mailbox's signature, formatted and plain. Kept apart from the general
    /// mailbox update so that an administrator changing, say, a quota cannot
    /// wipe it. Both empty when none is set.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<(string Html, string Text)> GetSignatureAsync(System.Guid mailboxId, System.Threading.CancellationToken ct = default);

    /// <summary>Set a mailbox's signature (already sanitised by the caller).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="html">Formatted form.</param>
    /// <param name="text">Plain form.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task SetSignatureAsync(System.Guid mailboxId, string html, string text, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Create or update a personal sender rule for one mailbox. Personal rules
    /// come from that mailbox's own Report spam and Not spam, and affect no
    /// other mailbox.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="pattern">An address, or "@domain".</param>
    /// <param name="action">Block (file in Junk) or allow.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<SenderRuleRow> UpsertMailboxSenderRuleAsync(System.Guid mailboxId, string pattern, SenderRuleAction action, System.Threading.CancellationToken ct = default);

    /// <summary>A mailbox's personal sender rules, by pattern.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<SenderRuleRow>> ListMailboxSenderRulesAsync(System.Guid mailboxId, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Delete a personal sender rule. Matches on both the mailbox and the rule
    /// id, so a rule can only ever be removed by the mailbox that owns it.
    /// </summary>
    /// <param name="mailboxId">The owning mailbox.</param>
    /// <param name="ruleId">The rule.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<bool> DeleteMailboxSenderRuleAsync(System.Guid mailboxId, System.Guid ruleId, System.Threading.CancellationToken ct = default);
}
