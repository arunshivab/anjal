using Npgsql;

namespace Anjal.Store;

/// <summary>rc.15 (items 58 and 61) in the PostgreSQL store: folder sizes, mail by receiving domain, and service records.</summary>
public sealed partial class PostgresMessageStore
{
    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<System.Guid, long>> SumFolderBytesAsync(System.Guid mailboxId, CancellationToken ct = default)
    {
        const string sql = "SELECT folder_id, coalesce(sum(size_bytes), 0)::bigint FROM messages WHERE mailbox_id = @mailbox GROUP BY folder_id;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("mailbox", mailboxId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new Dictionary<System.Guid, long>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result[r.GetGuid(0)] = r.GetInt64(1);
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RecipientDomainTraffic>> CountOutboundByRecipientDomainAsync(System.DateTimeOffset periodStart, System.DateTimeOffset periodEnd, CancellationToken ct = default)
    {
        const string sql = @"
SELECT lower(trim(trailing '>' from split_part(envelope_to, '@', 2))) AS domain,
       count(*) FILTER (WHERE status = 2),
       count(*) FILTER (WHERE status IN (0, 1)),
       count(*) FILTER (WHERE status = 3)
FROM outbound_messages
WHERE created_at >= @from AND created_at < @to
GROUP BY domain
ORDER BY domain;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("from", periodStart);
        cmd.Parameters.AddWithValue("to", periodEnd);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<RecipientDomainTraffic>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new RecipientDomainTraffic(r.IsDBNull(0) ? string.Empty : r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SenderDomainTraffic>> CountOutboundBySenderDomainAsync(System.DateTimeOffset periodStart, System.DateTimeOffset periodEnd, CancellationToken ct = default)
    {
        const string sql = @"
SELECT lower(trim(trailing '>' from split_part(envelope_from, '@', 2))) AS domain,
       count(*) FILTER (WHERE status = 2),
       count(*) FILTER (WHERE status IN (0, 1)),
       count(*) FILTER (WHERE status = 3)
FROM outbound_messages
WHERE created_at >= @from AND created_at < @to
GROUP BY domain
ORDER BY domain;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("from", periodStart);
        cmd.Parameters.AddWithValue("to", periodEnd);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<SenderDomainTraffic>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new SenderDomainTraffic(r.IsDBNull(0) ? string.Empty : r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<long?> DatabaseBytesAsync(CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT pg_database_size(current_database());", conn);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is long size ? size : null;
    }

    /// <inheritdoc/>
    public async Task<string?> GetServiceRecordAsync(string kind, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(kind);
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT body FROM service_records WHERE kind = @kind;", conn);
        cmd.Parameters.AddWithValue("kind", kind);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    /// <inheritdoc/>
    public async Task SetServiceRecordAsync(string kind, string json, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(kind);
        System.ArgumentNullException.ThrowIfNull(json);
        const string sql = @"
INSERT INTO service_records (kind, body, updated_at)
VALUES (@kind, @body, now())
ON CONFLICT (kind) DO UPDATE SET body = EXCLUDED.body, updated_at = now();";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("body", json);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
