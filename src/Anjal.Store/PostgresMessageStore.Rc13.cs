using Npgsql;

namespace Anjal.Store;

/// <summary>
/// rc.13 additions to the PostgreSQL store: an organisation's own documents
/// (<c>tenant_documents</c>) - see <c>tools/sql/schema.sql</c>.
/// </summary>
public sealed partial class PostgresMessageStore
{
    /// <inheritdoc/>
    public async Task<System.DateTimeOffset?> OldestPendingOutboundAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT min(created_at) FROM outbound_messages WHERE status = 0;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is System.DateTime dt ? new System.DateTimeOffset(System.DateTime.SpecifyKind(dt, System.DateTimeKind.Utc)) : result as System.DateTimeOffset?;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<(System.DateTimeOffset Hour, long Count)>> CountArrivalsByHourAsync(System.DateTimeOffset since, CancellationToken ct = default)
    {
        const string sql = @"
SELECT date_trunc('hour', received_at AT TIME ZONE 'UTC') AS h, count(*)
FROM messages WHERE received_at >= @since
GROUP BY h ORDER BY h;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("since", since);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<(System.DateTimeOffset, long)>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            System.DateTime hour = System.DateTime.SpecifyKind(reader.GetDateTime(0), System.DateTimeKind.Utc);
            result.Add((new System.DateTimeOffset(hour), reader.GetInt64(1)));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<string?> GetTenantDocumentAsync(System.Guid tenantId, string kind, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(kind);
        const string sql = "SELECT body FROM tenant_documents WHERE tenant_id = @tenant AND kind = @kind;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("kind", kind);
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as string;
    }

    /// <inheritdoc/>
    public async Task SetTenantDocumentAsync(System.Guid tenantId, string kind, string json, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(kind);
        System.ArgumentNullException.ThrowIfNull(json);
        const string sql = @"
INSERT INTO tenant_documents (tenant_id, kind, body, updated_at)
VALUES (@tenant, @kind, @body, now())
ON CONFLICT (tenant_id, kind) DO UPDATE SET body = EXCLUDED.body, updated_at = now();";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("body", json);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
