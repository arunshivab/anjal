
namespace Anjal.Store;

/// <summary>
/// In-memory implementation of <see cref="IMessageStore"/> used in tests
/// and the inbound-end-to-end example. Thread-safe via a single lock; not
/// optimised for high concurrency. No persistence across process restarts.
/// </summary>
public sealed class InMemoryMessageStore : IMessageStore
{
    private readonly object gate = new();
    private readonly List<RoutingRule> rules = new();
    private readonly List<TagGrant> grants = new();
    private readonly List<InboundMessage> messages = new();
    private readonly List<WebhookDelivery> deliveries = new();

    /// <summary>The routing rules currently stored. Provided for inspection in tests.</summary>
    public IReadOnlyList<RoutingRule> Rules
    {
        get
        {
            lock (this.gate)
            {
                return this.rules.ToArray();
            }
        }
    }

    /// <summary>The inbound messages currently stored. Provided for inspection in tests.</summary>
    public IReadOnlyList<InboundMessage> Messages
    {
        get
        {
            lock (this.gate)
            {
                return this.messages.ToArray();
            }
        }
    }

    /// <summary>The webhook deliveries currently stored. Provided for inspection in tests.</summary>
    public IReadOnlyList<WebhookDelivery> Deliveries
    {
        get
        {
            lock (this.gate)
            {
                return this.deliveries.ToArray();
            }
        }
    }

    /// <inheritdoc/>
    public Task<RoutingRule> UpsertRoutingRuleAsync(RoutingRule rule, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(rule);
        lock (this.gate)
        {
            int existing = this.rules.FindIndex(r => string.Equals(r.LocalPart, rule.LocalPart, System.StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                rule.Id = this.rules[existing].Id;
                rule.CreatedAt = this.rules[existing].CreatedAt;
                this.rules[existing] = rule;
            }
            else
            {
                rule.Id = System.Guid.NewGuid();
                if (rule.CreatedAt == default)
                {
                    rule.CreatedAt = System.DateTimeOffset.UtcNow;
                }
                this.rules.Add(rule);
            }
            return Task.FromResult(rule);
        }
    }

    /// <inheritdoc/>
    public Task<RoutingRule?> GetRoutingRuleAsync(string localPart, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(localPart);
        lock (this.gate)
        {
            RoutingRule? found = this.rules.Find(r => string.Equals(r.LocalPart, localPart, System.StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<RoutingRule>> ListRoutingRulesAsync(CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<RoutingRule> snapshot = this.rules.ToArray();
            return Task.FromResult(snapshot);
        }
    }

    /// <inheritdoc/>
    public Task<bool> DeleteRoutingRuleAsync(string localPart, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(localPart);
        lock (this.gate)
        {
            int removed = this.rules.RemoveAll(r => string.Equals(r.LocalPart, localPart, System.StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(removed > 0);
        }
    }

    /// <inheritdoc/>
    public Task<TagGrant> CreateTagGrantAsync(TagGrant grant, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(grant);
        lock (this.gate)
        {
            grant.Id = System.Guid.NewGuid();
            if (grant.CreatedAt == default)
            {
                grant.CreatedAt = System.DateTimeOffset.UtcNow;
            }
            this.grants.Add(grant);
            return Task.FromResult(grant);
        }
    }

    /// <inheritdoc/>
    public Task<TagGrant?> GetActiveTagGrantAsync(string localPart, string tag, System.DateTimeOffset asOf, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(localPart);
        System.ArgumentNullException.ThrowIfNull(tag);
        lock (this.gate)
        {
            TagGrant? found = this.grants.Find(g =>
                string.Equals(g.LocalPart, localPart, System.StringComparison.OrdinalIgnoreCase) &&
                string.Equals(g.Tag, tag, System.StringComparison.OrdinalIgnoreCase) &&
                g.ExpiresAt > asOf);
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc/>
    public Task<InboundMessage> SaveInboundMessageAsync(InboundMessage message, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(message);
        lock (this.gate)
        {
            message.Id = System.Guid.NewGuid();
            if (message.ReceivedAt == default)
            {
                message.ReceivedAt = System.DateTimeOffset.UtcNow;
            }
            this.messages.Add(message);
            return Task.FromResult(message);
        }
    }

    /// <inheritdoc/>
    public Task<WebhookDelivery> SaveWebhookDeliveryAsync(WebhookDelivery delivery, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(delivery);
        lock (this.gate)
        {
            delivery.Id = System.Guid.NewGuid();
            if (delivery.AttemptedAt == default)
            {
                delivery.AttemptedAt = System.DateTimeOffset.UtcNow;
            }
            this.deliveries.Add(delivery);
            return Task.FromResult(delivery);
        }
    }
}
