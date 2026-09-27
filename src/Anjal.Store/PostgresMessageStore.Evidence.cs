using Npgsql;

namespace Anjal.Store;

/// <summary>v1.0.0-rc.8 evidence store in PostgreSQL (ANJAL-DES-01).</summary>
public sealed partial class PostgresMessageStore
{
    private const string EvidenceColumns = "id, direction, captured_at, envelope_from, envelope_to, remote_address, client_hostname, transport_tls, authenticated_user, size_bytes, sha256, path, reconstructed, outcome, retention_days, all_copies_deleted_at, purge_after, purged_at, purge_reason, sent_message_id";

    /// <inheritdoc/>
    public async Task InsertEvidenceAsync(EvidenceRow row, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(row);
        const string sql = @"INSERT INTO evidence (id, direction, captured_at, envelope_from, envelope_to, remote_address, client_hostname, transport_tls, authenticated_user, size_bytes, sha256, path, reconstructed, outcome, retention_days, sent_message_id)
VALUES (@id, @direction, @captured_at, @envelope_from, @envelope_to, @remote_address, @client_hostname, @transport_tls, @authenticated_user, @size_bytes, @sha256, @path, @reconstructed, @outcome, @retention_days, @sent_message_id);";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", row.Id);
        cmd.Parameters.AddWithValue("direction", row.Direction);
        cmd.Parameters.AddWithValue("captured_at", row.CapturedAt.ToUniversalTime());
        cmd.Parameters.AddWithValue("envelope_from", row.EnvelopeFrom);
        cmd.Parameters.AddWithValue("envelope_to", row.EnvelopeTo.ToArray());
        cmd.Parameters.AddWithValue("remote_address", row.RemoteAddress);
        cmd.Parameters.AddWithValue("client_hostname", row.ClientHostName);
        cmd.Parameters.Add(new NpgsqlParameter<string?>("transport_tls", NpgsqlTypes.NpgsqlDbType.Text) { TypedValue = row.TransportTls });
        cmd.Parameters.Add(new NpgsqlParameter<string?>("authenticated_user", NpgsqlTypes.NpgsqlDbType.Text) { TypedValue = row.AuthenticatedUser });
        cmd.Parameters.AddWithValue("size_bytes", row.SizeBytes);
        cmd.Parameters.AddWithValue("sha256", row.Sha256);
        cmd.Parameters.AddWithValue("path", row.Path);
        cmd.Parameters.AddWithValue("reconstructed", row.Reconstructed);
        cmd.Parameters.AddWithValue("outcome", row.Outcome);
        cmd.Parameters.AddWithValue("retention_days", row.RetentionDays);
        cmd.Parameters.Add(new NpgsqlParameter<System.Guid?>("sent_message_id", NpgsqlTypes.NpgsqlDbType.Uuid) { TypedValue = row.SentMessageId });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<(System.Guid? EvidenceId, System.Guid? SentMessageId)> GetOutboundEvidenceLinkAsync(System.Guid outboundId, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT evidence_id, sent_message_id FROM outbound_messages WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", outboundId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return (null, null);
        }
        return (r.IsDBNull(0) ? null : r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1));
    }

    /// <inheritdoc/>
    public async Task SetOutboundEvidenceAsync(System.Guid outboundId, System.Guid evidenceId, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("UPDATE outbound_messages SET evidence_id = @e WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", outboundId);
        cmd.Parameters.AddWithValue("e", evidenceId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task LinkOutboundToSentCopyAsync(IReadOnlyList<System.Guid> outboundIds, System.Guid sentMessageId, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(outboundIds);
        if (outboundIds.Count == 0)
        {
            return;
        }
        const string sql = @"UPDATE outbound_messages SET sent_message_id = @s WHERE id = ANY(@ids);
UPDATE evidence SET sent_message_id = @s WHERE id IN (SELECT evidence_id FROM outbound_messages WHERE id = ANY(@ids) AND evidence_id IS NOT NULL);";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("s", sentMessageId);
        cmd.Parameters.AddWithValue("ids", outboundIds.ToArray());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> StartClocksForOutboundWithoutMailboxCopyAsync(System.DateTimeOffset capturedBefore, CancellationToken ct = default)
    {
        const string sql = @"UPDATE evidence
   SET all_copies_deleted_at = captured_at,
       purge_after = captured_at + make_interval(days => retention_days)
 WHERE direction = 'out' AND sent_message_id IS NULL AND all_copies_deleted_at IS NULL AND captured_at < @before;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("before", capturedBefore.ToUniversalTime());
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SetEvidenceOutcomeAsync(System.Guid id, string outcome, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(outcome);
        // A copy of mail that was not accepted is due for purging at once: the
        // sender still has the message, and we never delivered it.
        const string sql = @"UPDATE evidence SET outcome = @outcome,
    purge_after = CASE WHEN @outcome = 'not-accepted' THEN now() ELSE purge_after END
WHERE id = @id;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("outcome", outcome);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task RaiseEvidenceRetentionAsync(System.Guid id, int retentionDays, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("UPDATE evidence SET retention_days = GREATEST(retention_days, @days) WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("days", retentionDays);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<EvidenceRow?> GetEvidenceAsync(System.Guid id, CancellationToken ct = default)
    {
        IReadOnlyList<EvidenceRow> rows = await this.QueryEvidenceAsync("WHERE id = @a", id, null, int.MaxValue, ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<EvidenceRow>> ListEvidenceCapturedAsync(System.DateTimeOffset startInclusive, System.DateTimeOffset endExclusive, CancellationToken ct = default) =>
        this.QueryEvidenceAsync("WHERE captured_at >= @a AND captured_at < @b ORDER BY captured_at, id", startInclusive.ToUniversalTime(), endExclusive.ToUniversalTime(), int.MaxValue, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<EvidenceRow>> ListEvidencePurgedAsync(System.DateTimeOffset startInclusive, System.DateTimeOffset endExclusive, CancellationToken ct = default) =>
        this.QueryEvidenceAsync("WHERE purged_at >= @a AND purged_at < @b ORDER BY purged_at, id", startInclusive.ToUniversalTime(), endExclusive.ToUniversalTime(), int.MaxValue, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<EvidenceRow>> ListEvidenceDueAsync(System.DateTimeOffset now, int limit, CancellationToken ct = default) =>
        this.QueryEvidenceAsync("WHERE purged_at IS NULL AND purge_after <= @a ORDER BY purge_after, id", now.ToUniversalTime(), null, limit, ct);

    /// <inheritdoc/>
    public async Task MarkEvidencePurgedAsync(System.Guid id, System.DateTimeOffset at, string reason, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(reason);
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("UPDATE evidence SET purged_at = @at, purge_reason = @reason WHERE id = @id AND purged_at IS NULL;", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("at", at.ToUniversalTime());
        cmd.Parameters.AddWithValue("reason", reason);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task AddEvidenceAttemptAsync(EvidenceAttemptRow row, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(row);
        const string sql = @"INSERT INTO evidence_attempts (evidence_id, attempted_at, recipient, remote_host, transport_tls, reply_code, reply_text, outcome)
VALUES (@e, @at, @rcpt, @host, @tls, @code, @text, @outcome);";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("e", row.EvidenceId);
        cmd.Parameters.AddWithValue("at", row.AttemptedAt.ToUniversalTime());
        cmd.Parameters.AddWithValue("rcpt", row.Recipient);
        cmd.Parameters.AddWithValue("host", row.RemoteHost);
        cmd.Parameters.Add(new NpgsqlParameter<string?>("tls", NpgsqlTypes.NpgsqlDbType.Text) { TypedValue = row.TransportTls });
        cmd.Parameters.Add(new NpgsqlParameter<int?>("code", NpgsqlTypes.NpgsqlDbType.Integer) { TypedValue = row.ReplyCode });
        cmd.Parameters.AddWithValue("text", row.ReplyText);
        cmd.Parameters.AddWithValue("outcome", row.Outcome);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<EvidenceAttemptRow>> ListEvidenceAttemptsAsync(System.Guid id, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT evidence_id, attempted_at, recipient, remote_host, transport_tls, reply_code, reply_text, outcome FROM evidence_attempts WHERE evidence_id = @e ORDER BY attempted_at, id;", conn);
        cmd.Parameters.AddWithValue("e", id);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<EvidenceAttemptRow>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new EvidenceAttemptRow
            {
                EvidenceId = r.GetGuid(0),
                AttemptedAt = r.GetFieldValue<System.DateTimeOffset>(1),
                Recipient = r.GetString(2),
                RemoteHost = r.GetString(3),
                TransportTls = r.IsDBNull(4) ? null : r.GetString(4),
                ReplyCode = r.IsDBNull(5) ? null : r.GetInt32(5),
                ReplyText = r.GetString(6),
                Outcome = r.GetString(7),
            });
        }
        return list;
    }

    /// <inheritdoc/>
    public async Task UpdateMessageTransportAsync(System.Guid messageId, bool? encrypted, string? tls, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("UPDATE messages SET transport_encrypted = @e, transport_tls = @t WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", messageId);
        cmd.Parameters.Add(new NpgsqlParameter<bool?>("e", NpgsqlTypes.NpgsqlDbType.Boolean) { TypedValue = encrypted });
        cmd.Parameters.Add(new NpgsqlParameter<string?>("t", NpgsqlTypes.NpgsqlDbType.Text) { TypedValue = tls });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> SetMessageEvidenceAsync(System.Guid messageId, System.Guid evidenceId, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("UPDATE messages SET evidence_id = @e WHERE id = @id AND evidence_id IS NULL;", conn);
        cmd.Parameters.AddWithValue("id", messageId);
        cmd.Parameters.AddWithValue("e", evidenceId);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task<EvidenceManifestRow?> GetLatestEvidenceManifestAsync(CancellationToken ct = default)
    {
        IReadOnlyList<EvidenceManifestRow> all = await this.ReadManifestsAsync("ORDER BY day DESC LIMIT 1", ct).ConfigureAwait(false);
        return all.Count == 0 ? null : all[0];
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<EvidenceManifestRow>> ListEvidenceManifestsAsync(CancellationToken ct = default) =>
        this.ReadManifestsAsync("ORDER BY day", ct);

    /// <inheritdoc/>
    public async Task InsertEvidenceManifestAsync(EvidenceManifestRow row, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(row);
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("INSERT INTO evidence_manifests (day, sha256, previous_sha256, added, purged, path) VALUES (@d, @h, @p, @a, @u, @path);", conn);
        cmd.Parameters.AddWithValue("d", row.Day);
        cmd.Parameters.AddWithValue("h", row.Sha256);
        cmd.Parameters.AddWithValue("p", row.PreviousSha256);
        cmd.Parameters.AddWithValue("a", row.Added);
        cmd.Parameters.AddWithValue("u", row.Purged);
        cmd.Parameters.AddWithValue("path", row.Path);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<EvidenceManifestRow>> ReadManifestsAsync(string tail, CancellationToken ct)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT day, sha256, previous_sha256, added, purged, path FROM evidence_manifests " + tail + ";", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<EvidenceManifestRow>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new EvidenceManifestRow
            {
                Day = r.GetFieldValue<System.DateOnly>(0),
                Sha256 = r.GetString(1),
                PreviousSha256 = r.GetString(2),
                Added = r.GetInt32(3),
                Purged = r.GetInt32(4),
                Path = r.GetString(5),
            });
        }
        return list;
    }

    private async Task<IReadOnlyList<EvidenceRow>> QueryEvidenceAsync(string where, object a, object? b, int limit, CancellationToken ct)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT " + EvidenceColumns + " FROM evidence " + where + (limit < int.MaxValue ? " LIMIT " + limit.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty) + ";", conn);
        cmd.Parameters.AddWithValue("a", a);
        if (b is not null)
        {
            cmd.Parameters.AddWithValue("b", b);
        }
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<EvidenceRow>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new EvidenceRow
            {
                Id = r.GetGuid(0),
                Direction = r.GetString(1),
                CapturedAt = r.GetFieldValue<System.DateTimeOffset>(2),
                EnvelopeFrom = r.GetString(3),
                EnvelopeTo = r.GetFieldValue<string[]>(4),
                RemoteAddress = r.GetString(5),
                ClientHostName = r.GetString(6),
                TransportTls = r.IsDBNull(7) ? null : r.GetString(7),
                AuthenticatedUser = r.IsDBNull(8) ? null : r.GetString(8),
                SizeBytes = r.GetInt64(9),
                Sha256 = r.GetString(10),
                Path = r.GetString(11),
                Reconstructed = r.GetBoolean(12),
                Outcome = r.GetString(13),
                RetentionDays = r.GetInt32(14),
                AllCopiesDeletedAt = r.IsDBNull(15) ? null : r.GetFieldValue<System.DateTimeOffset>(15),
                PurgeAfter = r.IsDBNull(16) ? null : r.GetFieldValue<System.DateTimeOffset>(16),
                PurgedAt = r.IsDBNull(17) ? null : r.GetFieldValue<System.DateTimeOffset>(17),
                PurgeReason = r.IsDBNull(18) ? null : r.GetString(18),
                SentMessageId = r.IsDBNull(19) ? null : r.GetGuid(19),
            });
        }
        return list;
    }
}
