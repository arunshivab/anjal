namespace Anjal.Store;

/// <summary>rc.13 and rc.14 additions to the in-memory store: an organisation's own documents, and what the Anjal console counts.</summary>
public sealed partial class InMemoryMessageStore
{
    /// <inheritdoc/>
    public Task<System.DateTimeOffset?> OldestPendingOutboundAsync(CancellationToken ct = default)
    {
        lock (this.gate)
        {
            System.DateTimeOffset? oldest = this.outbound.Where(m => m.Status == OutboundStatus.Pending).Select(m => (System.DateTimeOffset?)m.CreatedAt).DefaultIfEmpty(null).Min();
            return Task.FromResult(oldest);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<(System.DateTimeOffset Hour, long Count)>> CountArrivalsByHourAsync(System.DateTimeOffset since, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<(System.DateTimeOffset, long)> result = this.mailboxMessages
                .Where(m => m.ReceivedAt >= since)
                .GroupBy(m => new System.DateTimeOffset(m.ReceivedAt.UtcDateTime.Year, m.ReceivedAt.UtcDateTime.Month, m.ReceivedAt.UtcDateTime.Day, m.ReceivedAt.UtcDateTime.Hour, 0, 0, System.TimeSpan.Zero))
                .OrderBy(g => g.Key)
                .Select(g => (g.Key, (long)g.Count()))
                .ToList();
            return Task.FromResult(result);
        }
    }

    private readonly Dictionary<(System.Guid, string), string> tenantDocuments = new();

    /// <inheritdoc/>
    public Task<string?> GetTenantDocumentAsync(System.Guid tenantId, string kind, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(kind);
        lock (this.gate)
        {
            return Task.FromResult(this.tenantDocuments.TryGetValue((tenantId, kind), out string? json) ? json : null);
        }
    }

    /// <inheritdoc/>
    public Task SetTenantDocumentAsync(System.Guid tenantId, string kind, string json, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(kind);
        System.ArgumentNullException.ThrowIfNull(json);
        lock (this.gate)
        {
            this.tenantDocuments[(tenantId, kind)] = json;
        }
        return Task.CompletedTask;
    }
}
