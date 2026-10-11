namespace Anjal.Store;

/// <summary>rc.15 (items 58 and 61) in the in-memory store: folder sizes, mail by receiving domain, and service records.</summary>
public sealed partial class InMemoryMessageStore
{
    private readonly Dictionary<string, string> serviceRecords = new(System.StringComparer.Ordinal);

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<System.Guid, long>> SumFolderBytesAsync(System.Guid mailboxId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyDictionary<System.Guid, long> result = this.mailboxMessages
                .Where(m => m.MailboxId == mailboxId)
                .GroupBy(m => m.FolderId)
                .ToDictionary(g => g.Key, g => g.Sum(m => m.SizeBytes));
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<RecipientDomainTraffic>> CountOutboundByRecipientDomainAsync(System.DateTimeOffset periodStart, System.DateTimeOffset periodEnd, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<RecipientDomainTraffic> result = this.outbound
                .Where(o => o.CreatedAt >= periodStart && o.CreatedAt < periodEnd)
                .GroupBy(o => DomainOf(o.EnvelopeTo))
                .Select(g => new RecipientDomainTraffic(
                    g.Key,
                    g.Count(o => o.Status == OutboundStatus.Sent),
                    g.Count(o => o.Status is OutboundStatus.Pending or OutboundStatus.Sending),
                    g.Count(o => o.Status == OutboundStatus.Failed)))
                .OrderBy(t => t.Domain, System.StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SenderDomainTraffic>> CountOutboundBySenderDomainAsync(System.DateTimeOffset periodStart, System.DateTimeOffset periodEnd, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<SenderDomainTraffic> result = this.outbound
                .Where(o => o.CreatedAt >= periodStart && o.CreatedAt < periodEnd)
                .GroupBy(o => DomainOf(o.EnvelopeFrom))
                .Select(g => new SenderDomainTraffic(
                    g.Key,
                    g.Count(o => o.Status == OutboundStatus.Sent),
                    g.Count(o => o.Status is OutboundStatus.Pending or OutboundStatus.Sending),
                    g.Count(o => o.Status == OutboundStatus.Failed)))
                .OrderBy(t => t.Domain, System.StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc/>
    public Task<long?> DatabaseBytesAsync(CancellationToken ct = default) => Task.FromResult<long?>(null);

    /// <inheritdoc/>
    public Task<string?> GetServiceRecordAsync(string kind, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(kind);
        lock (this.gate)
        {
            return Task.FromResult(this.serviceRecords.TryGetValue(kind, out string? json) ? json : null);
        }
    }

    /// <inheritdoc/>
    public Task SetServiceRecordAsync(string kind, string json, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(kind);
        System.ArgumentNullException.ThrowIfNull(json);
        lock (this.gate)
        {
            this.serviceRecords[kind] = json;
        }
        return Task.CompletedTask;
    }

    private static string DomainOf(string address)
    {
        int at = (address ?? string.Empty).LastIndexOf('@');
        return at < 0 ? string.Empty : address![(at + 1)..].Trim().TrimEnd('>').ToLowerInvariant();
    }
}
