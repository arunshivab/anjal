namespace Anjal.Store;

/// <summary>rc.17 additions to the in-memory store: the queries behind Sending systems.</summary>
public sealed partial class InMemoryMessageStore
{
    /// <inheritdoc/>
    public Task<IReadOnlyList<OutboundMessage>> ListOutboundBySendersAsync(IReadOnlyCollection<string> senders, System.DateTimeOffset start, System.DateTimeOffset until, int limit, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(senders);
        var from = new HashSet<string>(senders, System.StringComparer.OrdinalIgnoreCase);
        lock (this.gate)
        {
            IReadOnlyList<OutboundMessage> found = this.outbound
                .Where(m => from.Contains(m.EnvelopeFrom) && m.CreatedAt >= start && m.CreatedAt < until)
                .OrderByDescending(m => m.CreatedAt)
                .Take(System.Math.Max(0, limit))
                .Select(m => new OutboundMessage
                {
                    Id = m.Id,
                    EnvelopeFrom = m.EnvelopeFrom,
                    EnvelopeTo = m.EnvelopeTo,
                    RawBytes = m.RawBytes.Take(8192).ToArray(),
                    Status = m.Status,
                    Attempts = m.Attempts,
                    NextAttemptAt = m.NextAttemptAt,
                    CreatedAt = m.CreatedAt,
                    GiveUpAt = m.GiveUpAt,
                    LastError = m.LastError,
                    LeaseExpiresAt = m.LeaseExpiresAt,
                })
                .ToArray();
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<WebhookJob>> ListWebhookJobsAsync(IReadOnlyCollection<string> domains, System.DateTimeOffset since, int limit, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(domains);
        var at = new HashSet<string>(domains, System.StringComparer.OrdinalIgnoreCase);
        lock (this.gate)
        {
            IReadOnlyList<WebhookJob> found = this.webhookJobs
                .Where(j => j.CreatedAt >= since && at.Contains(j.Recipient[(j.Recipient.LastIndexOf('@') + 1)..]))
                .OrderByDescending(j => j.CreatedAt)
                .Take(System.Math.Max(0, limit))
                .ToArray();
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<WebhookDelivery>> ListWebhookDeliveriesAsync(IReadOnlyCollection<System.Guid> inboundMessageIds, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(inboundMessageIds);
        var ids = new HashSet<System.Guid>(inboundMessageIds);
        lock (this.gate)
        {
            IReadOnlyList<WebhookDelivery> found = this.deliveries.Where(d => ids.Contains(d.InboundMessageId)).OrderByDescending(d => d.AttemptedAt).ToArray();
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc/>
    public Task<bool> ReplayWebhookJobAsync(System.Guid id, System.DateTimeOffset now, System.DateTimeOffset giveUpAt, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            WebhookJob? j = this.webhookJobs.Find(x => x.Id == id);
            if (j is null || j.Status == WebhookJobStatus.Sending)
            {
                return Task.FromResult(false);
            }
            j.Status = WebhookJobStatus.Pending;
            j.Attempts = 0;
            j.NextAttemptAt = now;
            j.GiveUpAt = giveUpAt;
            j.LeaseExpiresAt = null;
            j.LastError = string.Empty;
            return Task.FromResult(true);
        }
    }
}
