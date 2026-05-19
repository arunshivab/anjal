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
}
