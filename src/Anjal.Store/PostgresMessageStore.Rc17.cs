using Npgsql;

namespace Anjal.Store;

/// <summary>rc.17 additions to the PostgreSQL store: the queries behind Sending systems.</summary>
public sealed partial class PostgresMessageStore
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<OutboundMessage>> ListOutboundBySendersAsync(IReadOnlyCollection<string> senders, System.DateTimeOffset start, System.DateTimeOffset until, int limit, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(senders);
        if (senders.Count == 0 || limit <= 0)
        {
            return System.Array.Empty<OutboundMessage>();
        }
        const string sql = @"
SELECT id, envelope_from, envelope_to, substring(raw_bytes from 1 for 8192), status, attempts, created_at, next_attempt_at, give_up_at, last_error
FROM outbound_messages
WHERE lower(envelope_from) = ANY(@senders) AND created_at >= @start AND created_at < @end
ORDER BY created_at DESC
LIMIT @limit;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("senders", senders.Select(s => s.Trim().ToLowerInvariant()).Distinct().ToArray());
        cmd.Parameters.AddWithValue("start", start);
        cmd.Parameters.AddWithValue("end", until);
        cmd.Parameters.AddWithValue("limit", limit);
        var result = new List<OutboundMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadOutbound(reader));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WebhookJob>> ListWebhookJobsAsync(IReadOnlyCollection<string> domains, System.DateTimeOffset since, int limit, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(domains);
        if (domains.Count == 0 || limit <= 0)
        {
            return System.Array.Empty<WebhookJob>();
        }
        const string sql = @"
SELECT id, inbound_message_id, recipient, local_part, tag, correlation_key, auth_results_json,
       status, attempts, next_attempt_at, lease_expires_at, give_up_at, last_error, created_at
FROM webhook_jobs
WHERE created_at >= @since AND lower(split_part(recipient, '@', 2)) = ANY(@domains)
ORDER BY created_at DESC
LIMIT @limit;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("domains", domains.Select(d => d.Trim().ToLowerInvariant()).Distinct().ToArray());
        cmd.Parameters.AddWithValue("since", since);
        cmd.Parameters.AddWithValue("limit", limit);
        var result = new List<WebhookJob>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadWebhookJob(reader));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WebhookDelivery>> ListWebhookDeliveriesAsync(IReadOnlyCollection<System.Guid> inboundMessageIds, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(inboundMessageIds);
        if (inboundMessageIds.Count == 0)
        {
            return System.Array.Empty<WebhookDelivery>();
        }
        const string sql = @"
SELECT id, inbound_message_id, url, status_code, attempted_at, error_message
FROM webhook_deliveries
WHERE inbound_message_id = ANY(@ids)
ORDER BY attempted_at DESC
LIMIT 2000;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("ids", inboundMessageIds.Distinct().ToArray());
        var result = new List<WebhookDelivery>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new WebhookDelivery
            {
                Id = reader.GetGuid(0),
                InboundMessageId = reader.GetGuid(1),
                Url = reader.GetString(2),
                StatusCode = reader.GetInt32(3),
                AttemptedAt = reader.GetFieldValue<System.DateTimeOffset>(4),
                ErrorMessage = reader.GetString(5),
            });
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<bool> ReplayWebhookJobAsync(System.Guid id, System.DateTimeOffset now, System.DateTimeOffset giveUpAt, CancellationToken ct = default)
    {
        const string sql = @"
UPDATE webhook_jobs
SET status = 0, attempts = 0, next_attempt_at = @now, give_up_at = @give_up, lease_expires_at = NULL, last_error = ''
WHERE id = @id AND status <> 1;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("give_up", giveUpAt);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }
}
