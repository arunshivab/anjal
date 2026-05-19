using Npgsql;

namespace Anjal.Store;

/// <summary>
/// PostgreSQL-backed implementation of <see cref="IMessageStore"/>. Uses
/// Npgsql for connection management; each method opens and closes its own
/// connection through the driver's connection pool. Connection-string is
/// supplied at construction time.
/// </summary>
public sealed class PostgresMessageStore : IMessageStore
{
    private readonly string connectionString;

    /// <summary>
    /// Create the store with a PostgreSQL connection string.
    /// </summary>
    /// <param name="connectionString">A PostgreSQL connection string,
    /// e.g. <c>Host=localhost;Database=anjal;Username=postgres;Password=...</c>.</param>
    public PostgresMessageStore(string connectionString)
    {
        System.ArgumentNullException.ThrowIfNull(connectionString);
        this.connectionString = connectionString;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(this.connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    /// <inheritdoc/>
    public async Task<RoutingRule> UpsertRoutingRuleAsync(RoutingRule rule, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(rule);

        const string sql = @"
INSERT INTO routing_rules (local_part, webhook_url, webhook_secret)
VALUES (@local_part, @webhook_url, @webhook_secret)
ON CONFLICT (local_part) DO UPDATE
    SET webhook_url    = EXCLUDED.webhook_url,
        webhook_secret = EXCLUDED.webhook_secret
RETURNING id, local_part, webhook_url, webhook_secret, created_at;";

        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("local_part", rule.LocalPart);
        cmd.Parameters.AddWithValue("webhook_url", rule.WebhookUrl);
        cmd.Parameters.AddWithValue("webhook_secret", rule.WebhookSecret);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return ReadRule(reader);
    }

    /// <inheritdoc/>
    public async Task<RoutingRule?> GetRoutingRuleAsync(string localPart, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(localPart);

        const string sql = @"
SELECT id, local_part, webhook_url, webhook_secret, created_at
FROM routing_rules
WHERE local_part = @local_part
LIMIT 1;";

        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("local_part", localPart);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }
        return ReadRule(reader);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RoutingRule>> ListRoutingRulesAsync(CancellationToken ct = default)
    {
        const string sql = @"
SELECT id, local_part, webhook_url, webhook_secret, created_at
FROM routing_rules
ORDER BY created_at;";

        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var result = new List<RoutingRule>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadRule(reader));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteRoutingRuleAsync(string localPart, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(localPart);
        const string sql = "DELETE FROM routing_rules WHERE local_part = @local_part;";

        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("local_part", localPart);
        int rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    /// <inheritdoc/>
    public async Task<TagGrant> CreateTagGrantAsync(TagGrant grant, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(grant);

        const string sql = @"
INSERT INTO tag_grants (local_part, tag, correlation_key, expires_at)
VALUES (@local_part, @tag, @correlation_key, @expires_at)
RETURNING id, local_part, tag, correlation_key, created_at, expires_at;";

        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("local_part", grant.LocalPart);
        cmd.Parameters.AddWithValue("tag", grant.Tag);
        cmd.Parameters.AddWithValue("correlation_key", grant.CorrelationKey ?? string.Empty);
        cmd.Parameters.AddWithValue("expires_at", grant.ExpiresAt);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return ReadGrant(reader);
    }

    /// <inheritdoc/>
    public async Task<TagGrant?> GetActiveTagGrantAsync(string localPart, string tag, System.DateTimeOffset asOf, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(localPart);
        System.ArgumentNullException.ThrowIfNull(tag);

        const string sql = @"
SELECT id, local_part, tag, correlation_key, created_at, expires_at
FROM tag_grants
WHERE local_part = @local_part
  AND tag = @tag
  AND expires_at > @as_of
ORDER BY expires_at DESC
LIMIT 1;";

        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("local_part", localPart);
        cmd.Parameters.AddWithValue("tag", tag);
        cmd.Parameters.AddWithValue("as_of", asOf);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }
        return ReadGrant(reader);
    }

    /// <inheritdoc/>
    public async Task<InboundMessage> SaveInboundMessageAsync(InboundMessage message, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(message);

        const string sql = @"
INSERT INTO inbound_messages
    (envelope_from, envelope_to, local_part, tag, message_id, subject, raw_bytes)
VALUES
    (@envelope_from, @envelope_to, @local_part, @tag, @message_id, @subject, @raw_bytes)
RETURNING id, envelope_from, envelope_to, local_part, tag, message_id, subject, received_at, raw_bytes;";

        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("envelope_from", message.EnvelopeFrom ?? string.Empty);
        cmd.Parameters.AddWithValue("envelope_to", message.EnvelopeTo ?? string.Empty);
        cmd.Parameters.AddWithValue("local_part", message.LocalPart ?? string.Empty);
        cmd.Parameters.AddWithValue("tag", message.Tag ?? string.Empty);
        cmd.Parameters.AddWithValue("message_id", message.MessageId ?? string.Empty);
        cmd.Parameters.AddWithValue("subject", message.Subject ?? string.Empty);
        cmd.Parameters.AddWithValue("raw_bytes", message.RawBytes ?? System.Array.Empty<byte>());

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return ReadMessage(reader);
    }

    /// <inheritdoc/>
    public async Task<WebhookDelivery> SaveWebhookDeliveryAsync(WebhookDelivery delivery, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(delivery);

        const string sql = @"
INSERT INTO webhook_deliveries
    (inbound_message_id, url, status_code, error_message)
VALUES
    (@inbound_message_id, @url, @status_code, @error_message)
RETURNING id, inbound_message_id, url, status_code, attempted_at, error_message;";

        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("inbound_message_id", delivery.InboundMessageId);
        cmd.Parameters.AddWithValue("url", delivery.Url ?? string.Empty);
        cmd.Parameters.AddWithValue("status_code", delivery.StatusCode);
        cmd.Parameters.AddWithValue("error_message", delivery.ErrorMessage ?? string.Empty);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return ReadDelivery(reader);
    }

    /// <inheritdoc/>
    public async Task<OutboundMessage> EnqueueOutboundAsync(OutboundMessage message, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(message);

        System.DateTimeOffset now = System.DateTimeOffset.UtcNow;
        System.DateTimeOffset next = message.NextAttemptAt == default ? now : message.NextAttemptAt;
        System.DateTimeOffset giveUp = message.GiveUpAt == default ? now.AddHours(24) : message.GiveUpAt;

        const string sql = @"
INSERT INTO outbound_messages
    (envelope_from, envelope_to, raw_bytes, status, attempts, next_attempt_at, give_up_at, last_error)
VALUES
    (@envelope_from, @envelope_to, @raw_bytes, @status, 0, @next_attempt_at, @give_up_at, '')
RETURNING id, envelope_from, envelope_to, raw_bytes, status, attempts, created_at, next_attempt_at, give_up_at, last_error;";

        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("envelope_from", message.EnvelopeFrom ?? string.Empty);
        cmd.Parameters.AddWithValue("envelope_to", message.EnvelopeTo ?? string.Empty);
        cmd.Parameters.AddWithValue("raw_bytes", message.RawBytes ?? System.Array.Empty<byte>());
        cmd.Parameters.AddWithValue("status", (int)OutboundStatus.Pending);
        cmd.Parameters.AddWithValue("next_attempt_at", next);
        cmd.Parameters.AddWithValue("give_up_at", giveUp);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return ReadOutbound(reader);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OutboundMessage>> LeaseOutboundBatchAsync(int batchSize, System.DateTimeOffset now, CancellationToken ct = default)
    {
        if (batchSize <= 0)
        {
            throw new System.ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be positive.");
        }

        // CTE pattern with SELECT ... FOR UPDATE SKIP LOCKED to safely lease
        // rows across multiple workers. UPDATE then RETURNING to flip status.
        const string sql = @"
WITH due AS (
    SELECT id
    FROM outbound_messages
    WHERE status = @pending_status
      AND next_attempt_at <= @now
    ORDER BY next_attempt_at ASC
    LIMIT @batch_size
    FOR UPDATE SKIP LOCKED
)
UPDATE outbound_messages o
SET status = @sending_status
FROM due
WHERE o.id = due.id
RETURNING o.id, o.envelope_from, o.envelope_to, o.raw_bytes, o.status, o.attempts, o.created_at, o.next_attempt_at, o.give_up_at, o.last_error;";

        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("pending_status", (int)OutboundStatus.Pending);
        cmd.Parameters.AddWithValue("sending_status", (int)OutboundStatus.Sending);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("batch_size", batchSize);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<OutboundMessage>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadOutbound(reader));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<OutboundMessage?> MarkOutboundResultAsync(
        System.Guid id,
        OutboundStatus newStatus,
        System.DateTimeOffset nextAttemptAt,
        string lastError,
        CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(lastError);

        const string sql = @"
UPDATE outbound_messages
SET status          = @status,
    attempts        = attempts + 1,
    next_attempt_at = @next_attempt_at,
    last_error      = @last_error
WHERE id = @id
RETURNING id, envelope_from, envelope_to, raw_bytes, status, attempts, created_at, next_attempt_at, give_up_at, last_error;";

        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("status", (int)newStatus);
        cmd.Parameters.AddWithValue("next_attempt_at", nextAttemptAt);
        cmd.Parameters.AddWithValue("last_error", lastError);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }
        return ReadOutbound(reader);
    }

    private static RoutingRule ReadRule(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        LocalPart = r.GetString(1),
        WebhookUrl = r.GetString(2),
        WebhookSecret = r.GetString(3),
        CreatedAt = r.GetFieldValue<System.DateTimeOffset>(4),
    };

    private static TagGrant ReadGrant(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        LocalPart = r.GetString(1),
        Tag = r.GetString(2),
        CorrelationKey = r.GetString(3),
        CreatedAt = r.GetFieldValue<System.DateTimeOffset>(4),
        ExpiresAt = r.GetFieldValue<System.DateTimeOffset>(5),
    };

    private static InboundMessage ReadMessage(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        EnvelopeFrom = r.GetString(1),
        EnvelopeTo = r.GetString(2),
        LocalPart = r.GetString(3),
        Tag = r.GetString(4),
        MessageId = r.GetString(5),
        Subject = r.GetString(6),
        ReceivedAt = r.GetFieldValue<System.DateTimeOffset>(7),
        RawBytes = (byte[])r.GetValue(8),
    };

    private static WebhookDelivery ReadDelivery(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        InboundMessageId = r.GetGuid(1),
        Url = r.GetString(2),
        StatusCode = r.GetInt32(3),
        AttemptedAt = r.GetFieldValue<System.DateTimeOffset>(4),
        ErrorMessage = r.GetString(5),
    };

    private static OutboundMessage ReadOutbound(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        EnvelopeFrom = r.GetString(1),
        EnvelopeTo = r.GetString(2),
        RawBytes = (byte[])r.GetValue(3),
        Status = (OutboundStatus)r.GetInt32(4),
        Attempts = r.GetInt32(5),
        CreatedAt = r.GetFieldValue<System.DateTimeOffset>(6),
        NextAttemptAt = r.GetFieldValue<System.DateTimeOffset>(7),
        GiveUpAt = r.GetFieldValue<System.DateTimeOffset>(8),
        LastError = r.GetString(9),
    };
}
