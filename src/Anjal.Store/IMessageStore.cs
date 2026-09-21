namespace Anjal.Store;

/// <summary>
/// Persistence interface for the Anjal mail server. Implementations include
/// an in-memory store (for tests and demos) and a PostgreSQL store (for
/// production). The interface is async-first because production code paths
/// hit the network.
/// </summary>
public interface IMessageStore
{
    // -------- Routing rules --------

    /// <summary>
    /// Insert or replace a routing rule keyed by its local-part. Replacing
    /// preserves the existing identifier if a row with the same local-part
    /// already exists. Returns the saved rule with <see cref="RoutingRule.Id"/>
    /// populated.
    /// </summary>
    System.Threading.Tasks.Task<RoutingRule> UpsertRoutingRuleAsync(RoutingRule rule, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Look up the routing rule for a given local-part (case-insensitive).
    /// Returns <see langword="null"/> if no rule exists.
    /// </summary>
    System.Threading.Tasks.Task<RoutingRule?> GetRoutingRuleAsync(string localPart, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// List all routing rules in insertion order.
    /// </summary>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<RoutingRule>> ListRoutingRulesAsync(System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Remove the rule for a given local-part. Returns <see langword="true"/>
    /// if a rule was deleted.
    /// </summary>
    System.Threading.Tasks.Task<bool> DeleteRoutingRuleAsync(string localPart, System.Threading.CancellationToken ct = default);

    // -------- Tag grants --------

    /// <summary>
    /// Create a new tag grant for time-bounded per-case authorisation.
    /// Returns the saved grant with <see cref="TagGrant.Id"/> populated.
    /// </summary>
    System.Threading.Tasks.Task<TagGrant> CreateTagGrantAsync(TagGrant grant, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Look up an active (unexpired) grant by (local-part, tag). Returns
    /// <see langword="null"/> if no matching grant exists or all matching
    /// grants have expired.
    /// </summary>
    System.Threading.Tasks.Task<TagGrant?> GetActiveTagGrantAsync(string localPart, string tag, System.DateTimeOffset asOf, System.Threading.CancellationToken ct = default);

    // -------- Inbound messages and delivery log --------

    /// <summary>
    /// Persist an inbound message. Returns the saved message with
    /// <see cref="InboundMessage.Id"/> populated.
    /// </summary>
    System.Threading.Tasks.Task<InboundMessage> SaveInboundMessageAsync(InboundMessage message, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Record a webhook delivery outcome for diagnostics.
    /// </summary>
    System.Threading.Tasks.Task<WebhookDelivery> SaveWebhookDeliveryAsync(WebhookDelivery delivery, System.Threading.CancellationToken ct = default);

    /// <summary>Queue a webhook notification. <see cref="WebhookJob.Id"/> and <see cref="WebhookJob.CreatedAt"/> are assigned.</summary>
    System.Threading.Tasks.Task<WebhookJob> EnqueueWebhookJobAsync(WebhookJob job, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Lease due notifications, including any whose previous lease lapsed
    /// (a worker stopped mid-attempt). Leased jobs move to Sending.
    /// </summary>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<WebhookJob>> LeaseWebhookJobsAsync(int batchSize, System.DateTimeOffset now, System.Threading.CancellationToken ct = default);

    /// <summary>Record an attempt's outcome and schedule the next one if Pending.</summary>
    System.Threading.Tasks.Task CompleteWebhookJobAsync(System.Guid id, WebhookJobStatus status, System.DateTimeOffset nextAttemptAt, string lastError, System.Threading.CancellationToken ct = default);

    /// <summary>Count queued notifications by state, for metrics.</summary>
    System.Threading.Tasks.Task<long> CountWebhookJobsAsync(WebhookJobStatus status, System.Threading.CancellationToken ct = default);

    // -------- Outbound queue --------

    /// <summary>
    /// Enqueue an outbound message for delivery. Sets status to
    /// <see cref="OutboundStatus.Pending"/> and assigns identifiers.
    /// </summary>
    /// <param name="message">The message to enqueue.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The persisted message with <see cref="OutboundMessage.Id"/> populated.</returns>
    System.Threading.Tasks.Task<OutboundMessage> EnqueueOutboundAsync(OutboundMessage message, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Lease up to <paramref name="batchSize"/> messages whose next-attempt time
    /// has passed. Leased messages have their status flipped to
    /// <see cref="OutboundStatus.Sending"/> so other workers won't pick them up.
    /// The worker must call <see cref="MarkOutboundResultAsync"/> after each
    /// attempt to release the lease (success, retry, or give up).
    /// </summary>
    /// <param name="batchSize">Max messages to lease.</param>
    /// <param name="now">The current time; used both to filter and to update timestamps.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The leased messages, in order of their next-attempt time.</returns>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<OutboundMessage>> LeaseOutboundBatchAsync(int batchSize, System.DateTimeOffset now, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Record the result of an outbound send attempt. Updates status,
    /// increments the attempt counter, sets next-attempt-at for retries,
    /// and writes the last-error text.
    /// </summary>
    /// <param name="id">The outbound message identifier.</param>
    /// <param name="newStatus">The new status (Pending for retry, Sent, or Failed).</param>
    /// <param name="nextAttemptAt">Time of next attempt for retries; ignored for terminal states.</param>
    /// <param name="lastError">Most recent reply or error text.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The updated row, or <see langword="null"/> if no such id exists.</returns>
    System.Threading.Tasks.Task<OutboundMessage?> MarkOutboundResultAsync(
        System.Guid id,
        OutboundStatus newStatus,
        System.DateTimeOffset nextAttemptAt,
        string lastError,
        System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Fetch a single outbound message by id. Returns <see langword="null"/>
    /// if not found.
    /// </summary>
    /// <param name="id">The outbound message identifier.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<OutboundMessage?> GetOutboundByIdAsync(System.Guid id, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Count outbound messages by status. Used by <c>/metrics</c> for queue
    /// depth; a cheap indexed count.
    /// </summary>
    /// <param name="status">The status to count.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<long> CountOutboundAsync(OutboundStatus status, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Append an entry to the audit trail. Entries are never updated or
    /// deleted; in PostgreSQL a trigger refuses any attempt to.
    /// </summary>
    /// <param name="audit">The event; <see cref="AuditEvent.Id"/> and <see cref="AuditEvent.At"/> are assigned.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<AuditEvent> AppendAuditAsync(AuditEvent audit, System.Threading.CancellationToken ct = default);

    /// <summary>The most recent audit entries, newest first.</summary>
    /// <param name="limit">How many.</param>
    /// <param name="before">Only entries strictly before this time, for paging; null for the newest.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<AuditEvent>> ListAuditAsync(int limit, System.DateTimeOffset? before = null, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Fetch a single inbound message by id. Returns <see langword="null"/>
    /// if not found.
    /// </summary>
    /// <param name="id">The inbound message identifier.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<InboundMessage?> GetInboundByIdAsync(System.Guid id, System.Threading.CancellationToken ct = default);

    // -------- Outbound TLS policies --------

    /// <summary>
    /// Create or update a TLS policy for a destination domain. The domain
    /// is matched case-insensitively.
    /// </summary>
    /// <param name="policy">The policy to upsert.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The persisted policy with <see cref="OutboundTlsPolicy.Id"/> populated.</returns>
    System.Threading.Tasks.Task<OutboundTlsPolicy> UpsertOutboundTlsPolicyAsync(OutboundTlsPolicy policy, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// List all configured TLS policies.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<OutboundTlsPolicy>> ListOutboundTlsPoliciesAsync(System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Look up the TLS policy for a destination domain. Returns
    /// <see langword="null"/> if no specific policy exists - the caller
    /// applies the configured default in that case.
    /// </summary>
    /// <param name="domain">The destination domain (case-insensitive).</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<OutboundTlsPolicy?> GetOutboundTlsPolicyAsync(string domain, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Remove a policy for a destination domain.
    /// </summary>
    /// <param name="domain">The destination domain (case-insensitive).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns><see langword="true"/> if a policy was removed, <see langword="false"/> if none existed.</returns>
    System.Threading.Tasks.Task<bool> DeleteOutboundTlsPolicyAsync(string domain, System.Threading.CancellationToken ct = default);

    // -------- DKIM keys --------

    /// <summary>
    /// Create or update a DKIM signing key for a sender domain. The
    /// (domain, selector) pair is unique - upserting with the same domain
    /// rotates the key or selector.
    /// </summary>
    /// <param name="key">The key to upsert.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The persisted key with <see cref="DkimKeyRow.Id"/> populated.</returns>
    System.Threading.Tasks.Task<DkimKeyRow> UpsertDkimKeyAsync(DkimKeyRow key, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// List all configured DKIM keys. The <see cref="DkimKeyRow.PrivateKeyPem"/>
    /// field IS populated; callers handling API responses should redact it.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<DkimKeyRow>> ListDkimKeysAsync(System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Look up the DKIM signing key for a sender domain.
    /// </summary>
    /// <param name="domain">The sender domain (case-insensitive).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The key, or <see langword="null"/> if none configured for that domain.</returns>
    System.Threading.Tasks.Task<DkimKeyRow?> GetDkimKeyAsync(string domain, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Remove the DKIM key for a sender domain.
    /// </summary>
    /// <param name="domain">The sender domain (case-insensitive).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns><see langword="true"/> if a key was removed.</returns>
    System.Threading.Tasks.Task<bool> DeleteDkimKeyAsync(string domain, System.Threading.CancellationToken ct = default);

    // -------- SMTP submission users --------

    /// <summary>
    /// Create or update an SMTP submission user. Username is unique
    /// case-insensitively. The password should already be PBKDF2-hashed
    /// before calling this method - the store does not hash for you.
    /// </summary>
    /// <param name="user">User to upsert.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The persisted row with Id populated.</returns>
    System.Threading.Tasks.Task<SmtpUserRow> UpsertSmtpUserAsync(SmtpUserRow user, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// List all configured SMTP users. Hashes ARE populated; callers
    /// handling API responses must redact the hash field.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<SmtpUserRow>> ListSmtpUsersAsync(System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Look up an SMTP user by username (case-insensitive).
    /// </summary>
    /// <param name="username">Username to look up.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The user row, or null if no such user.</returns>
    System.Threading.Tasks.Task<SmtpUserRow?> GetSmtpUserAsync(string username, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Remove an SMTP user by username.
    /// </summary>
    /// <param name="username">Username to delete.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True if a user was removed.</returns>
    System.Threading.Tasks.Task<bool> DeleteSmtpUserAsync(string username, System.Threading.CancellationToken ct = default);

    // -------- Local domains --------

    /// <summary>
    /// Register a domain as local. Idempotent: adding the same domain
    /// twice is not an error.
    /// </summary>
    /// <param name="domain">Domain to register.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<LocalDomainRow> UpsertLocalDomainAsync(string domain, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// List all local domains.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<LocalDomainRow>> ListLocalDomainsAsync(System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Check whether a domain is local (case-insensitive).
    /// </summary>
    /// <param name="domain">Domain to check.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<bool> IsLocalDomainAsync(string domain, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Remove a local domain.
    /// </summary>
    /// <param name="domain">Domain to remove.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True if a row was removed.</returns>
    System.Threading.Tasks.Task<bool> DeleteLocalDomainAsync(string domain, System.Threading.CancellationToken ct = default);
}
