
namespace Anjal.Store;

/// <summary>
/// In-memory implementation of <see cref="IMessageStore"/> used in tests
/// and the inbound-end-to-end example. Thread-safe via a single lock; not
/// optimised for high concurrency. No persistence across process restarts.
/// </summary>
public sealed partial class InMemoryMessageStore : IMessageStore, IMailboxStore
{
    private readonly object gate = new();
    private readonly List<RoutingRule> rules = new();
    private readonly List<TagGrant> grants = new();
    private readonly List<InboundMessage> messages = new();
    private readonly List<WebhookDelivery> deliveries = new();
    private readonly List<OutboundMessage> outbound = new();
    private readonly List<OutboundTlsPolicy> tlsPolicies = new();
    private readonly List<DkimKeyRow> dkimKeys = new();
    private readonly List<SmtpUserRow> smtpUsers = new();
    private readonly List<LocalDomainRow> localDomains = new();

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

    /// <summary>The outbound messages currently queued or completed. Provided for inspection in tests.</summary>
    public IReadOnlyList<OutboundMessage> Outbound
    {
        get
        {
            lock (this.gate)
            {
                return this.outbound.ToArray();
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

    /// <inheritdoc/>
    public Task<OutboundMessage> EnqueueOutboundAsync(OutboundMessage message, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(message);
        lock (this.gate)
        {
            message.Id = System.Guid.NewGuid();
            message.Status = OutboundStatus.Pending;
            message.Attempts = 0;
            if (message.CreatedAt == default)
            {
                message.CreatedAt = System.DateTimeOffset.UtcNow;
            }
            if (message.NextAttemptAt == default)
            {
                message.NextAttemptAt = message.CreatedAt;
            }
            if (message.GiveUpAt == default)
            {
                message.GiveUpAt = message.CreatedAt.AddHours(24);
            }
            this.outbound.Add(message);
            return Task.FromResult(message);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<OutboundMessage>> LeaseOutboundBatchAsync(int batchSize, System.DateTimeOffset now, CancellationToken ct = default)
    {
        if (batchSize <= 0)
        {
            throw new System.ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be positive.");
        }
        lock (this.gate)
        {
            var leased = new List<OutboundMessage>();
            foreach (OutboundMessage m in this.outbound)
            {
                if (leased.Count >= batchSize)
                {
                    break;
                }
                if (m.Status == OutboundStatus.Pending && m.NextAttemptAt <= now)
                {
                    m.Status = OutboundStatus.Sending;
                    leased.Add(m);
                }
            }
            IReadOnlyList<OutboundMessage> result = leased.ToArray();
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc/>
    public Task<OutboundMessage?> MarkOutboundResultAsync(
        System.Guid id,
        OutboundStatus newStatus,
        System.DateTimeOffset nextAttemptAt,
        string lastError,
        CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(lastError);
        lock (this.gate)
        {
            OutboundMessage? found = this.outbound.Find(m => m.Id == id);
            if (found is null)
            {
                return Task.FromResult<OutboundMessage?>(null);
            }
            found.Status = newStatus;
            found.LastError = lastError;
            found.Attempts++;
            if (newStatus == OutboundStatus.Pending)
            {
                found.NextAttemptAt = nextAttemptAt;
            }
            return Task.FromResult<OutboundMessage?>(found);
        }
    }

    /// <inheritdoc/>
    public Task<long> CountOutboundAsync(OutboundStatus status, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            long n = 0;
            foreach (OutboundMessage m in this.outbound)
            {
                if (m.Status == status)
                {
                    n++;
                }
            }
            return Task.FromResult(n);
        }
    }

    /// <inheritdoc/>
    public Task<OutboundMessage?> GetOutboundByIdAsync(System.Guid id, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            OutboundMessage? found = this.outbound.Find(m => m.Id == id);
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc/>
    public Task<InboundMessage?> GetInboundByIdAsync(System.Guid id, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            InboundMessage? found = this.messages.Find(m => m.Id == id);
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc/>
    public Task<OutboundTlsPolicy> UpsertOutboundTlsPolicyAsync(OutboundTlsPolicy policy, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(policy);
        lock (this.gate)
        {
            string key = policy.Domain.ToLowerInvariant();
            OutboundTlsPolicy? existing = this.tlsPolicies.Find(p =>
                string.Equals(p.Domain, key, System.StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                policy.Id = System.Guid.NewGuid();
                policy.Domain = key;
                policy.UpdatedAt = System.DateTimeOffset.UtcNow;
                this.tlsPolicies.Add(policy);
                return Task.FromResult(policy);
            }
            existing.Mode = policy.Mode;
            existing.UpdatedAt = System.DateTimeOffset.UtcNow;
            return Task.FromResult(existing);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<OutboundTlsPolicy>> ListOutboundTlsPoliciesAsync(CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<OutboundTlsPolicy> snapshot = this.tlsPolicies.ToArray();
            return Task.FromResult(snapshot);
        }
    }

    /// <inheritdoc/>
    public Task<OutboundTlsPolicy?> GetOutboundTlsPolicyAsync(string domain, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(domain);
        lock (this.gate)
        {
            OutboundTlsPolicy? found = this.tlsPolicies.Find(p =>
                string.Equals(p.Domain, domain, System.StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc/>
    public Task<bool> DeleteOutboundTlsPolicyAsync(string domain, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(domain);
        lock (this.gate)
        {
            int removed = this.tlsPolicies.RemoveAll(p =>
                string.Equals(p.Domain, domain, System.StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(removed > 0);
        }
    }

    /// <inheritdoc/>
    public Task<DkimKeyRow> UpsertDkimKeyAsync(DkimKeyRow key, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(key);
        lock (this.gate)
        {
            string keyDomain = key.Domain.ToLowerInvariant();
            DkimKeyRow? existing = this.dkimKeys.Find(k =>
                string.Equals(k.Domain, keyDomain, System.StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                key.Id = System.Guid.NewGuid();
                key.Domain = keyDomain;
                key.UpdatedAt = System.DateTimeOffset.UtcNow;
                this.dkimKeys.Add(key);
                return Task.FromResult(key);
            }
            existing.Selector = key.Selector;
            existing.PrivateKeyPem = key.PrivateKeyPem;
            existing.UpdatedAt = System.DateTimeOffset.UtcNow;
            return Task.FromResult(existing);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<DkimKeyRow>> ListDkimKeysAsync(CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<DkimKeyRow> snapshot = this.dkimKeys.ToArray();
            return Task.FromResult(snapshot);
        }
    }

    /// <inheritdoc/>
    public Task<DkimKeyRow?> GetDkimKeyAsync(string domain, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(domain);
        lock (this.gate)
        {
            DkimKeyRow? found = this.dkimKeys.Find(k =>
                string.Equals(k.Domain, domain, System.StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc/>
    public Task<bool> DeleteDkimKeyAsync(string domain, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(domain);
        lock (this.gate)
        {
            int removed = this.dkimKeys.RemoveAll(k =>
                string.Equals(k.Domain, domain, System.StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(removed > 0);
        }
    }

    /// <inheritdoc/>
    public Task<SmtpUserRow> UpsertSmtpUserAsync(SmtpUserRow user, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(user);
        lock (this.gate)
        {
            SmtpUserRow? existing = this.smtpUsers.Find(u =>
                string.Equals(u.Username, user.Username, System.StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                user.Id = System.Guid.NewGuid();
                user.UpdatedAt = System.DateTimeOffset.UtcNow;
                this.smtpUsers.Add(user);
                return Task.FromResult(user);
            }
            existing.PasswordPbkdf2 = user.PasswordPbkdf2;
            existing.AllowedFromDomains = user.AllowedFromDomains;
            existing.Enabled = user.Enabled;
            existing.UpdatedAt = System.DateTimeOffset.UtcNow;
            return Task.FromResult(existing);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SmtpUserRow>> ListSmtpUsersAsync(CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<SmtpUserRow> snapshot = this.smtpUsers.ToArray();
            return Task.FromResult(snapshot);
        }
    }

    /// <inheritdoc/>
    public Task<SmtpUserRow?> GetSmtpUserAsync(string username, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(username);
        lock (this.gate)
        {
            SmtpUserRow? found = this.smtpUsers.Find(u =>
                string.Equals(u.Username, username, System.StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc/>
    public Task<bool> DeleteSmtpUserAsync(string username, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(username);
        lock (this.gate)
        {
            int removed = this.smtpUsers.RemoveAll(u =>
                string.Equals(u.Username, username, System.StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(removed > 0);
        }
    }

    /// <inheritdoc/>
    public Task<LocalDomainRow> UpsertLocalDomainAsync(string domain, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(domain);
        lock (this.gate)
        {
            string normalized = domain.ToLowerInvariant();
            LocalDomainRow? existing = this.localDomains.Find(d =>
                string.Equals(d.Domain, normalized, System.StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return Task.FromResult(existing);
            }
            var row = new LocalDomainRow
            {
                Id = System.Guid.NewGuid(),
                Domain = normalized,
                CreatedAt = System.DateTimeOffset.UtcNow,
            };
            this.localDomains.Add(row);
            return Task.FromResult(row);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<LocalDomainRow>> ListLocalDomainsAsync(CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<LocalDomainRow> snapshot = this.localDomains.ToArray();
            return Task.FromResult(snapshot);
        }
    }

    /// <inheritdoc/>
    public Task<bool> IsLocalDomainAsync(string domain, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(domain);
        lock (this.gate)
        {
            bool found = this.localDomains.Exists(d =>
                string.Equals(d.Domain, domain, System.StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc/>
    public Task<bool> DeleteLocalDomainAsync(string domain, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(domain);
        lock (this.gate)
        {
            int removed = this.localDomains.RemoveAll(d =>
                string.Equals(d.Domain, domain, System.StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(removed > 0);
        }
    }
}
