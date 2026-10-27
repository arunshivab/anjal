using System.Text.Json;
using Npgsql;

namespace Anjal.Store;

/// <summary>rc.26 (DES-14 §28.1): the application API's tables - see <c>tools/sql/schema.sql</c>.</summary>
public sealed partial class PostgresMessageStore : IAppApiStore
{
    private const string AppSendColumns = "id, tenant_id, app_id, created_at, scheduled_at, from_address, to_addresses, tags, metadata, idempotency_key, body_sha256, response, sandbox, template, template_version, message_id, status, last_event, evidence_id";

    private const string AppEventColumns = "seq, id, tenant_id, app_id, send_id, type, at, data";

    private const string AppDeliveryColumns = "id, event_seq, app_id, webhook_id, attempts, at, code, next_at, status, error";

    private const string AppAddressColumns = "address, tenant_id, app_id, mailbox_id, shared_mailbox_id, added_at";

    /// <inheritdoc/>
    public async Task<(AppSend Send, bool Existing)> StartAppSendAsync(AppSend send, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(send);
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        if (send.IdempotencyKey is { Length: > 0 } key)
        {
            const string find = "SELECT " + AppSendColumns + " FROM app_sends WHERE app_id = @app AND idempotency_key = @key AND created_at > now() - interval '24 hours';";
            await using (var look = new NpgsqlCommand(find, conn))
            {
                look.Parameters.AddWithValue("app", send.AppId);
                look.Parameters.AddWithValue("key", key);
                await using var r = await look.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    return (ReadAppSend(r), true);
                }
            }
            // An old use of the key (over 24 hours) gives way to the new one.
            await using var clear = new NpgsqlCommand("UPDATE app_sends SET idempotency_key = NULL WHERE app_id = @app AND idempotency_key = @key;", conn);
            clear.Parameters.AddWithValue("app", send.AppId);
            clear.Parameters.AddWithValue("key", key);
            await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        const string sql = @"
INSERT INTO app_sends (id, tenant_id, app_id, created_at, scheduled_at, from_address, to_addresses, tags, metadata, idempotency_key, body_sha256, response, sandbox, template, template_version, message_id, status, last_event)
VALUES (@id, @tenant, @app, @created, @scheduled, @from, @to, @tags, @metadata, @key, @sha, @response, @sandbox, @template, @version, @message_id, @status, @last_event)
ON CONFLICT DO NOTHING
RETURNING " + AppSendColumns + ";";
        await using (var cmd = new NpgsqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("id", send.Id);
            cmd.Parameters.AddWithValue("tenant", send.TenantId);
            cmd.Parameters.AddWithValue("app", send.AppId);
            cmd.Parameters.AddWithValue("created", (send.CreatedAt == default ? System.DateTimeOffset.UtcNow : send.CreatedAt).ToUniversalTime());
            cmd.Parameters.Add(new NpgsqlParameter<System.DateTimeOffset?>("scheduled", NpgsqlTypes.NpgsqlDbType.TimestampTz) { TypedValue = send.ScheduledAt?.ToUniversalTime() });
            cmd.Parameters.AddWithValue("from", send.From);
            cmd.Parameters.AddWithValue("to", JsonSerializer.Serialize(send.To));
            cmd.Parameters.AddWithValue("tags", JsonSerializer.Serialize(send.Tags));
            cmd.Parameters.AddWithValue("metadata", JsonSerializer.Serialize(send.Metadata));
            cmd.Parameters.Add(new NpgsqlParameter<string?>("key", NpgsqlTypes.NpgsqlDbType.Text) { TypedValue = send.IdempotencyKey is { Length: > 0 } ? send.IdempotencyKey : null });
            cmd.Parameters.AddWithValue("sha", send.BodySha256);
            cmd.Parameters.AddWithValue("response", send.Response);
            cmd.Parameters.AddWithValue("sandbox", send.Sandbox);
            cmd.Parameters.AddWithValue("template", send.Template);
            cmd.Parameters.AddWithValue("version", send.TemplateVersion);
            cmd.Parameters.AddWithValue("message_id", send.MessageId);
            cmd.Parameters.AddWithValue("status", send.Status);
            cmd.Parameters.AddWithValue("last_event", send.LastEvent);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                return (ReadAppSend(r), false);
            }
        }
        // Another request with the same key won the race: that one is the send.
        AppSend? other = send.IdempotencyKey is { Length: > 0 } k2 ? await FindByKeyAsync(conn, send.AppId, k2, ct).ConfigureAwait(false) : null;
        return other is null ? throw new System.InvalidOperationException("The send could not be recorded.") : (other, true);
    }

    private static async Task<AppSend?> FindByKeyAsync(NpgsqlConnection conn, string appId, string key, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT " + AppSendColumns + " FROM app_sends WHERE app_id = @app AND idempotency_key = @key;", conn);
        cmd.Parameters.AddWithValue("app", appId);
        cmd.Parameters.AddWithValue("key", key);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? ReadAppSend(r) : null;
    }

    /// <inheritdoc/>
    public async Task UpdateAppSendAsync(System.Guid id, string status, string? response, string? messageId, System.Guid? evidenceId = null, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(status);
        const string sql = "UPDATE app_sends SET status = @status, response = COALESCE(@response, response), message_id = COALESCE(@message_id, message_id), evidence_id = COALESCE(@evidence_id, evidence_id) WHERE id = @id;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.Add(new NpgsqlParameter<string?>("response", NpgsqlTypes.NpgsqlDbType.Text) { TypedValue = response });
        cmd.Parameters.Add(new NpgsqlParameter<string?>("message_id", NpgsqlTypes.NpgsqlDbType.Text) { TypedValue = messageId });
        cmd.Parameters.Add(new NpgsqlParameter<System.Guid?>("evidence_id", NpgsqlTypes.NpgsqlDbType.Uuid) { TypedValue = evidenceId });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteAppSendAsync(System.Guid id, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("DELETE FROM app_sends WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<AppSend?> GetAppSendAsync(System.Guid id, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT " + AppSendColumns + " FROM app_sends WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? ReadAppSend(r) : null;
    }

    /// <inheritdoc/>
    public async Task<AppSend?> FindAppSendByKeyAsync(string appId, string key, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(appId);
        System.ArgumentNullException.ThrowIfNull(key);
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT " + AppSendColumns + " FROM app_sends WHERE app_id = @app AND idempotency_key = @key AND created_at > now() - interval '24 hours';", conn);
        cmd.Parameters.AddWithValue("app", appId);
        cmd.Parameters.AddWithValue("key", key);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? ReadAppSend(r) : null;
    }

    /// <inheritdoc/>
    public async Task<AppSend?> FindAppSendByMessageIdAsync(string appId, string messageId, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(appId);
        System.ArgumentNullException.ThrowIfNull(messageId);
        if (messageId.Length == 0)
        {
            return null;
        }
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT " + AppSendColumns + " FROM app_sends WHERE app_id = @app AND message_id = @mid LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("app", appId);
        cmd.Parameters.AddWithValue("mid", messageId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? ReadAppSend(r) : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AppSend>> ListAppSendsAsync(string appId, AppSendQuery query, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(appId);
        System.ArgumentNullException.ThrowIfNull(query);
        const string sql = @"
SELECT " + AppSendColumns + @" FROM app_sends
WHERE app_id = @app AND status NOT IN ('batch', 'refused')
  AND (@since IS NULL OR created_at >= @since)
  AND (@until IS NULL OR created_at < @until)
  AND (@before IS NULL OR created_at < @before)
  AND (@tag = '' OR tags::jsonb ? @tag)
ORDER BY created_at DESC
LIMIT @limit;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("app", appId);
        cmd.Parameters.Add(new NpgsqlParameter<System.DateTimeOffset?>("since", NpgsqlTypes.NpgsqlDbType.TimestampTz) { TypedValue = query.Since?.ToUniversalTime() });
        cmd.Parameters.Add(new NpgsqlParameter<System.DateTimeOffset?>("until", NpgsqlTypes.NpgsqlDbType.TimestampTz) { TypedValue = query.Until?.ToUniversalTime() });
        cmd.Parameters.Add(new NpgsqlParameter<System.DateTimeOffset?>("before", NpgsqlTypes.NpgsqlDbType.TimestampTz) { TypedValue = query.Before?.ToUniversalTime() });
        cmd.Parameters.AddWithValue("tag", query.Tag ?? string.Empty);
        cmd.Parameters.AddWithValue("limit", System.Math.Clamp(query.Limit, 1, 1000));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<AppSend>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadAppSend(r));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<long> CountAppSendsAsync(string appId, System.DateTimeOffset since, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(appId);
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM app_sends WHERE app_id = @app AND created_at >= @since AND status NOT IN ('batch', 'refused');", conn);
        cmd.Parameters.AddWithValue("app", appId);
        cmd.Parameters.AddWithValue("since", since.ToUniversalTime());
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is long n ? n : 0;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OutboundMessage>> ListOutboundOfAppSendAsync(System.Guid sendId, CancellationToken ct = default)
    {
        const string sql = @"
SELECT id, envelope_from, envelope_to, ''::bytea, status, attempts, created_at, next_attempt_at, give_up_at, last_error, lease_expires_at, finished_at, body_removed_at, app_send_id
FROM outbound_messages WHERE app_send_id = @id ORDER BY created_at, envelope_to;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", sendId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<OutboundMessage>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadOutbound(r));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<long> CountAppOutboundElsewhereAsync(System.Guid tenantId, IReadOnlyCollection<string> ownDomains, System.DateTimeOffset start, System.DateTimeOffset until, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(ownDomains);
        const string sql = @"
SELECT count(*) FROM outbound_messages o JOIN app_sends s ON s.id = o.app_send_id
WHERE s.tenant_id = @t AND o.created_at >= @start AND o.created_at < @until
  AND NOT (lower(split_part(o.envelope_from, '@', 2)) = ANY(@own));";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("start", start.ToUniversalTime());
        cmd.Parameters.AddWithValue("until", until.ToUniversalTime());
        cmd.Parameters.AddWithValue("own", ownDomains.Select(d => d.ToLowerInvariant()).ToArray());
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is long n ? n : 0;
    }

    /// <inheritdoc/>
    public async Task<AppEvent> AddAppEventAsync(AppEvent appEvent, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(appEvent);
        const string sql = "INSERT INTO app_events (id, tenant_id, app_id, send_id, type, at, data) VALUES (@id, @tenant, @app, @send, @type, @at, @data) RETURNING " + AppEventColumns + ";";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", appEvent.Id == System.Guid.Empty ? System.Guid.NewGuid() : appEvent.Id);
        cmd.Parameters.AddWithValue("tenant", appEvent.TenantId);
        cmd.Parameters.AddWithValue("app", appEvent.AppId);
        cmd.Parameters.Add(new NpgsqlParameter<System.Guid?>("send", NpgsqlTypes.NpgsqlDbType.Uuid) { TypedValue = appEvent.SendId });
        cmd.Parameters.AddWithValue("type", appEvent.Type);
        cmd.Parameters.AddWithValue("at", (appEvent.At == default ? System.DateTimeOffset.UtcNow : appEvent.At).ToUniversalTime());
        cmd.Parameters.AddWithValue("data", appEvent.Data);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await r.ReadAsync(ct).ConfigureAwait(false);
        AppEvent added = ReadAppEvent(r);
        await r.DisposeAsync().ConfigureAwait(false);
        if (added.SendId is System.Guid sid)
        {
            await using var last = new NpgsqlCommand("UPDATE app_sends SET last_event = @type WHERE id = @id;", conn);
            last.Parameters.AddWithValue("type", added.Type);
            last.Parameters.AddWithValue("id", sid);
            await last.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return added;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AppEvent>> ListAppEventsAsync(string? appId, long afterSeq, int limit, CancellationToken ct = default)
    {
        const string sql = "SELECT " + AppEventColumns + " FROM app_events WHERE (@app::text IS NULL OR app_id = @app) AND seq > @after ORDER BY seq LIMIT @limit;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter<string?>("app", NpgsqlTypes.NpgsqlDbType.Text) { TypedValue = appId });
        cmd.Parameters.AddWithValue("after", afterSeq);
        cmd.Parameters.AddWithValue("limit", System.Math.Clamp(limit, 1, 5000));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<AppEvent>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadAppEvent(r));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AppEvent>> UndispatchedAppEventsAsync(int limit, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT " + AppEventColumns + " FROM app_events WHERE NOT dispatched ORDER BY seq LIMIT @limit;", conn);
        cmd.Parameters.AddWithValue("limit", System.Math.Clamp(limit, 1, 5000));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<AppEvent>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadAppEvent(r));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task MarkAppEventDispatchedAsync(long seq, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("UPDATE app_events SET dispatched = true WHERE seq = @seq;", conn);
        cmd.Parameters.AddWithValue("seq", seq);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<AppEvent?> GetAppEventAsync(long seq, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT " + AppEventColumns + " FROM app_events WHERE seq = @seq;", conn);
        cmd.Parameters.AddWithValue("seq", seq);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? ReadAppEvent(r) : null;
    }

    /// <inheritdoc/>
    public async Task SaveAppWebhookDeliveryAsync(AppWebhookDelivery delivery, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(delivery);
        const string sql = @"
INSERT INTO app_webhook_deliveries (" + AppDeliveryColumns + @")
VALUES (@id, @seq, @app, @hook, @attempts, @at, @code, @next, @status, @error)
ON CONFLICT (id) DO UPDATE SET attempts = EXCLUDED.attempts, at = EXCLUDED.at, code = EXCLUDED.code, next_at = EXCLUDED.next_at, status = EXCLUDED.status, error = EXCLUDED.error;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        if (delivery.Id == System.Guid.Empty)
        {
            delivery.Id = System.Guid.NewGuid();
        }
        cmd.Parameters.AddWithValue("id", delivery.Id);
        cmd.Parameters.AddWithValue("seq", delivery.EventSeq);
        cmd.Parameters.AddWithValue("app", delivery.AppId);
        cmd.Parameters.AddWithValue("hook", delivery.WebhookId);
        cmd.Parameters.AddWithValue("attempts", delivery.Attempts);
        cmd.Parameters.AddWithValue("at", (delivery.At == default ? System.DateTimeOffset.UtcNow : delivery.At).ToUniversalTime());
        cmd.Parameters.Add(new NpgsqlParameter<int?>("code", NpgsqlTypes.NpgsqlDbType.Integer) { TypedValue = delivery.Code });
        cmd.Parameters.Add(new NpgsqlParameter<System.DateTimeOffset?>("next", NpgsqlTypes.NpgsqlDbType.TimestampTz) { TypedValue = delivery.NextAt?.ToUniversalTime() });
        cmd.Parameters.AddWithValue("status", delivery.Status);
        cmd.Parameters.AddWithValue("error", delivery.Error);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AppWebhookDelivery>> DueAppWebhookDeliveriesAsync(System.DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        const string sql = "SELECT " + AppDeliveryColumns + " FROM app_webhook_deliveries WHERE status = 'pending' AND next_at <= @now ORDER BY next_at LIMIT @limit;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("now", now.ToUniversalTime());
        cmd.Parameters.AddWithValue("limit", System.Math.Clamp(limit, 1, 1000));
        return await ReadDeliveriesAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AppWebhookDelivery>> ListAppWebhookDeliveriesAsync(string appId, int limit, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(appId);
        const string sql = "SELECT " + AppDeliveryColumns + " FROM app_webhook_deliveries WHERE app_id = @app ORDER BY at DESC LIMIT @limit;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("app", appId);
        cmd.Parameters.AddWithValue("limit", System.Math.Clamp(limit, 1, 1000));
        return await ReadDeliveriesAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<AppWebhookDelivery?> GetAppWebhookDeliveryAsync(System.Guid id, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT " + AppDeliveryColumns + " FROM app_webhook_deliveries WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", id);
        IReadOnlyList<AppWebhookDelivery> found = await ReadDeliveriesAsync(cmd, ct).ConfigureAwait(false);
        return found.Count > 0 ? found[0] : null;
    }

    /// <inheritdoc/>
    public async Task<bool> RememberSignatureAsync(string keyId, string signature, System.DateTimeOffset now, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(keyId);
        System.ArgumentNullException.ThrowIfNull(signature);
        const string sql = @"
INSERT INTO app_signatures_seen (key_id, signature, seen_at) VALUES (@key, @sig, @now)
ON CONFLICT (key_id, signature) DO UPDATE SET seen_at = EXCLUDED.seen_at WHERE app_signatures_seen.seen_at < @now - interval '10 minutes'
RETURNING 1;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("key", keyId);
        cmd.Parameters.AddWithValue("sig", signature);
        cmd.Parameters.AddWithValue("now", now.ToUniversalTime());
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AppInboxAddress>> ListAppInboxAddressesAsync(System.Guid tenantId, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT " + AppAddressColumns + " FROM app_inbox_addresses WHERE tenant_id = @t ORDER BY address;", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<AppInboxAddress>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadAppAddress(r));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<AppInboxAddress?> GetAppInboxAddressAsync(string address, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(address);
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT " + AppAddressColumns + " FROM app_inbox_addresses WHERE address = @a;", conn);
        cmd.Parameters.AddWithValue("a", address.Trim().ToLowerInvariant());
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? ReadAppAddress(r) : null;
    }

    /// <inheritdoc/>
    public async Task SetAppInboxAddressAsync(AppInboxAddress address, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(address);
        const string sql = @"
INSERT INTO app_inbox_addresses (address, tenant_id, app_id, mailbox_id, shared_mailbox_id, added_at)
VALUES (@a, @t, @app, @m, @shared, @at)
ON CONFLICT (address) DO UPDATE SET tenant_id = EXCLUDED.tenant_id, app_id = EXCLUDED.app_id, mailbox_id = EXCLUDED.mailbox_id, shared_mailbox_id = EXCLUDED.shared_mailbox_id;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("a", address.Address.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("t", address.TenantId);
        cmd.Parameters.AddWithValue("app", address.AppId);
        cmd.Parameters.AddWithValue("m", address.MailboxId);
        cmd.Parameters.Add(new NpgsqlParameter<System.Guid?>("shared", NpgsqlTypes.NpgsqlDbType.Uuid) { TypedValue = address.SharedMailboxId });
        cmd.Parameters.AddWithValue("at", (address.AddedAt == default ? System.DateTimeOffset.UtcNow : address.AddedAt).ToUniversalTime());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAppInboxAddressAsync(string address, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(address);
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("DELETE FROM app_inbox_addresses WHERE address = @a;", conn);
        cmd.Parameters.AddWithValue("a", address.Trim().ToLowerInvariant());
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MessageRow>> ListAppInboxAsync(System.Guid mailboxId, bool? acknowledged, (System.DateTimeOffset At, System.Guid Id)? after, int limit, CancellationToken ct = default)
    {
        const string sql = "SELECT " + MessageColumns + @", body_text FROM messages
WHERE mailbox_id = @m
  AND (@ack IS NULL OR (acknowledged_at IS NOT NULL) = @ack)
  AND (@after_at IS NULL OR (received_at, id) > (@after_at, @after_id))
ORDER BY received_at, id
LIMIT @limit;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("m", mailboxId);
        cmd.Parameters.Add(new NpgsqlParameter<bool?>("ack", NpgsqlTypes.NpgsqlDbType.Boolean) { TypedValue = acknowledged });
        cmd.Parameters.Add(new NpgsqlParameter<System.DateTimeOffset?>("after_at", NpgsqlTypes.NpgsqlDbType.TimestampTz) { TypedValue = after?.At.ToUniversalTime() });
        cmd.Parameters.AddWithValue("after_id", after?.Id ?? System.Guid.Empty);
        cmd.Parameters.AddWithValue("limit", System.Math.Clamp(limit, 1, 1000));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<MessageRow>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            MessageRow row = ReadMailboxMessage(r);
            row.BodyText = r.GetString(26);
            result.Add(row);
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<bool> AcknowledgeAppMailAsync(System.Guid mailboxId, System.Guid messageId, System.DateTimeOffset at, CancellationToken ct = default)
    {
        const string sql = "UPDATE messages SET acknowledged_at = COALESCE(acknowledged_at, @at) WHERE mailbox_id = @m AND id = @id;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("m", mailboxId);
        cmd.Parameters.AddWithValue("id", messageId);
        cmd.Parameters.AddWithValue("at", at.ToUniversalTime());
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<(long Count, System.DateTimeOffset? Oldest)> AppInboxWaitingAsync(System.Guid mailboxId, CancellationToken ct = default)
    {
        const string sql = "SELECT count(*), min(received_at) FROM messages WHERE mailbox_id = @m AND acknowledged_at IS NULL;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("m", mailboxId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await r.ReadAsync(ct).ConfigureAwait(false);
        return (r.GetInt64(0), r.IsDBNull(1) ? null : r.GetFieldValue<System.DateTimeOffset>(1));
    }

    /// <inheritdoc/>
    public async Task<int> PruneAppApiAsync(System.DateTimeOffset now, CancellationToken ct = default)
    {
        const string sql = @"
WITH e AS (DELETE FROM app_events WHERE at < @now - interval '30 days' RETURNING 1),
     d AS (DELETE FROM app_webhook_deliveries WHERE at < @now - interval '30 days' AND status <> 'pending' RETURNING 1),
     s AS (DELETE FROM app_signatures_seen WHERE seen_at < @now - interval '10 minutes' RETURNING 1),
     k AS (UPDATE app_sends SET idempotency_key = NULL WHERE idempotency_key IS NOT NULL AND created_at < @now - interval '24 hours' RETURNING 1),
     b AS (DELETE FROM app_sends WHERE sandbox AND created_at < @now - interval '7 days' RETURNING 1)
SELECT (SELECT count(*) FROM e) + (SELECT count(*) FROM d) + (SELECT count(*) FROM s) + (SELECT count(*) FROM b);";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("now", now.ToUniversalTime());
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is long n ? (int)n : 0;
    }

    private static async Task<IReadOnlyList<AppWebhookDelivery>> ReadDeliveriesAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<AppWebhookDelivery>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new AppWebhookDelivery
            {
                Id = r.GetGuid(0),
                EventSeq = r.GetInt64(1),
                AppId = r.GetString(2),
                WebhookId = r.GetString(3),
                Attempts = r.GetInt32(4),
                At = r.GetFieldValue<System.DateTimeOffset>(5),
                Code = r.IsDBNull(6) ? null : r.GetInt32(6),
                NextAt = r.IsDBNull(7) ? null : r.GetFieldValue<System.DateTimeOffset>(7),
                Status = r.GetString(8),
                Error = r.GetString(9),
            });
        }
        return result;
    }

    private static AppSend ReadAppSend(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        TenantId = r.GetGuid(1),
        AppId = r.GetString(2),
        CreatedAt = r.GetFieldValue<System.DateTimeOffset>(3),
        ScheduledAt = r.IsDBNull(4) ? null : r.GetFieldValue<System.DateTimeOffset>(4),
        From = r.GetString(5),
        To = JsonSerializer.Deserialize<List<string>>(r.GetString(6)) ?? new List<string>(),
        Tags = JsonSerializer.Deserialize<List<string>>(r.GetString(7)) ?? new List<string>(),
        Metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(8)) ?? new Dictionary<string, string>(System.StringComparer.Ordinal),
        IdempotencyKey = r.IsDBNull(9) ? null : r.GetString(9),
        BodySha256 = r.GetString(10),
        Response = r.GetString(11),
        Sandbox = r.GetBoolean(12),
        Template = r.GetString(13),
        TemplateVersion = r.GetInt32(14),
        MessageId = r.GetString(15),
        Status = r.GetString(16),
        LastEvent = r.GetString(17),
        EvidenceId = r.IsDBNull(18) ? null : r.GetGuid(18),
    };

    private static AppEvent ReadAppEvent(NpgsqlDataReader r) => new()
    {
        Seq = r.GetInt64(0),
        Id = r.GetGuid(1),
        TenantId = r.GetGuid(2),
        AppId = r.GetString(3),
        SendId = r.IsDBNull(4) ? null : r.GetGuid(4),
        Type = r.GetString(5),
        At = r.GetFieldValue<System.DateTimeOffset>(6),
        Data = r.GetString(7),
    };

    private static AppInboxAddress ReadAppAddress(NpgsqlDataReader r) => new()
    {
        Address = r.GetString(0),
        TenantId = r.GetGuid(1),
        AppId = r.GetString(2),
        MailboxId = r.GetGuid(3),
        SharedMailboxId = r.IsDBNull(4) ? null : r.GetGuid(4),
        AddedAt = r.GetFieldValue<System.DateTimeOffset>(5),
    };
}
