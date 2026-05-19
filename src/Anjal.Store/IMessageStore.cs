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
}
