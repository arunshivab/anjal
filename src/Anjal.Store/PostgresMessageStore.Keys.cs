using Npgsql;
using NpgsqlTypes;

namespace Anjal.Store;

/// <summary>rc.25 (DES-15 §10): the key tables in PostgreSQL.</summary>
public sealed partial class PostgresMessageStore : IMailKeyStore
{
    private const string MailboxKeyColumns = "mailbox_id, public_key, unlock_salt, private_by_password, private_by_org, data_key, created_at, updated_at";
    private const string TenantKeyColumns = "tenant_id, recovery_public, recovery_by_secret, secret_check, sheet_confirmed_at, break_glass_on, secret_by_break_glass, recovery_by_server, created_at, updated_at";
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (byte[]? Key, DateTimeOffset At)> publicKeys = new();
    private (byte[]? Key, DateTimeOffset At) queueSeal;

    private const string RestoreColumns = "mailbox_id, restore_public, restore_by_unlock, unlock_salt, granted, granted_by, granted_at, created_at";

    /// <inheritdoc/>
    public async Task<MailboxKeyRow?> GetMailboxKeysAsync(Guid mailboxId, CancellationToken ct = default)
    {
        await using NpgsqlConnection conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand($"SELECT {MailboxKeyColumns} FROM mailbox_keys WHERE mailbox_id = @id;", conn);
        cmd.Parameters.AddWithValue("id", mailboxId);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? ReadMailboxKeys(r) : null;
    }

    /// <inheritdoc/>
    public async Task SaveMailboxKeysAsync(MailboxKeyRow row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        // The public key never changes: an update for another public key matches no row and is refused.
        const string sql = @"
INSERT INTO mailbox_keys (mailbox_id, public_key, unlock_salt, private_by_password, private_by_org, data_key, created_at, updated_at)
VALUES (@id, @pub, @salt, @pw, @org, @dk, now(), now())
ON CONFLICT (mailbox_id) DO UPDATE SET unlock_salt = EXCLUDED.unlock_salt,
    private_by_password = EXCLUDED.private_by_password, private_by_org = EXCLUDED.private_by_org,
    data_key = EXCLUDED.data_key, updated_at = now()
WHERE mailbox_keys.public_key = EXCLUDED.public_key;";
        if (await this.WriteMailboxKeysAsync(sql, row, ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("A mailbox's public key never changes.");
        }
    }

    /// <inheritdoc/>
    public async Task<bool> AddMailboxKeysAsync(MailboxKeyRow row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        const string sql = @"
INSERT INTO mailbox_keys (mailbox_id, public_key, unlock_salt, private_by_password, private_by_org, data_key, created_at, updated_at)
VALUES (@id, @pub, @salt, @pw, @org, @dk, now(), now())
ON CONFLICT (mailbox_id) DO NOTHING;";
        return await this.WriteMailboxKeysAsync(sql, row, ct).ConfigureAwait(false) == 1;
    }

    private async Task<int> WriteMailboxKeysAsync(string sql, MailboxKeyRow row, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", row.MailboxId);
        cmd.Parameters.AddWithValue("pub", row.PublicKey);
        cmd.Parameters.AddWithValue("salt", row.UnlockSalt);
        AddBytes(cmd, "pw", row.PrivateByPassword);
        cmd.Parameters.AddWithValue("org", row.PrivateByOrg);
        cmd.Parameters.AddWithValue("dk", row.DataKey);
        int written = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        this.publicKeys.TryRemove(row.MailboxId, out _);
        return written;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<Guid, byte[]>> GetPublicKeysAsync(IReadOnlyCollection<Guid> mailboxIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mailboxIds);
        var result = new Dictionary<Guid, byte[]>();
        if (mailboxIds.Count == 0)
        {
            return result;
        }
        await using NpgsqlConnection conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT mailbox_id, public_key FROM mailbox_keys WHERE mailbox_id = ANY(@ids);", conn);
        cmd.Parameters.AddWithValue("ids", mailboxIds.Distinct().ToArray());
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result[r.GetGuid(0)] = (byte[])r[1];
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<TenantKeyRow?> GetTenantKeysAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using NpgsqlConnection conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand($"SELECT {TenantKeyColumns} FROM tenant_keys WHERE tenant_id = @id;", conn);
        cmd.Parameters.AddWithValue("id", tenantId);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }
        return new TenantKeyRow
        {
            TenantId = r.GetGuid(0),
            RecoveryPublic = (byte[])r[1],
            RecoveryBySecret = (byte[])r[2],
            SecretCheck = r.GetString(3),
            SheetConfirmedAt = r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4),
            BreakGlassOn = r.GetBoolean(5),
            SecretByBreakGlass = r.IsDBNull(6) ? null : (byte[])r[6],
            RecoveryByServer = r.IsDBNull(7) ? null : (byte[])r[7],
            CreatedAt = r.GetFieldValue<DateTimeOffset>(8),
            UpdatedAt = r.GetFieldValue<DateTimeOffset>(9),
        };
    }

    /// <inheritdoc/>
    public async Task SaveTenantKeysAsync(TenantKeyRow row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        // The recovery public key never changes: an update for another key matches no row and is refused.
        const string sql = @"
INSERT INTO tenant_keys (tenant_id, recovery_public, recovery_by_secret, secret_check, sheet_confirmed_at, break_glass_on, secret_by_break_glass, recovery_by_server, created_at, updated_at)
VALUES (@id, @pub, @sec, @check, @confirmed, @bg, @bgsecret, @srv, now(), now())
ON CONFLICT (tenant_id) DO UPDATE SET recovery_by_secret = EXCLUDED.recovery_by_secret,
    secret_check = EXCLUDED.secret_check, sheet_confirmed_at = EXCLUDED.sheet_confirmed_at, break_glass_on = EXCLUDED.break_glass_on,
    secret_by_break_glass = EXCLUDED.secret_by_break_glass, recovery_by_server = EXCLUDED.recovery_by_server, updated_at = now()
WHERE tenant_keys.recovery_public = EXCLUDED.recovery_public;";
        if (await this.WriteTenantKeysAsync(sql, row, ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("An organisation's recovery public key never changes.");
        }
    }

    /// <inheritdoc/>
    public async Task<bool> AddTenantKeysAsync(TenantKeyRow row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        const string sql = @"
INSERT INTO tenant_keys (tenant_id, recovery_public, recovery_by_secret, secret_check, sheet_confirmed_at, break_glass_on, secret_by_break_glass, recovery_by_server, created_at, updated_at)
VALUES (@id, @pub, @sec, @check, @confirmed, @bg, @bgsecret, @srv, now(), now())
ON CONFLICT (tenant_id) DO NOTHING;";
        return await this.WriteTenantKeysAsync(sql, row, ct).ConfigureAwait(false) == 1;
    }

    private async Task<int> WriteTenantKeysAsync(string sql, TenantKeyRow row, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", row.TenantId);
        cmd.Parameters.AddWithValue("pub", row.RecoveryPublic);
        cmd.Parameters.AddWithValue("sec", row.RecoveryBySecret);
        cmd.Parameters.AddWithValue("check", row.SecretCheck);
        cmd.Parameters.Add(new NpgsqlParameter("confirmed", NpgsqlDbType.TimestampTz) { Value = row.SheetConfirmedAt is DateTimeOffset c ? c : DBNull.Value });
        cmd.Parameters.AddWithValue("bg", row.BreakGlassOn);
        AddBytes(cmd, "bgsecret", row.SecretByBreakGlass);
        AddBytes(cmd, "srv", row.RecoveryByServer);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<KeyGrant>> ListGrantsForHolderAsync(Guid holderId, CancellationToken ct = default)
    {
        await using NpgsqlConnection conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT key_owner, holder_id, kind, body FROM key_grants WHERE holder_id = @h;", conn);
        cmd.Parameters.AddWithValue("h", holderId);
        return await ReadGrantsAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<KeyGrant>> ListGrantsOfAsync(Guid keyOwner, string kind, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(kind);
        await using NpgsqlConnection conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT key_owner, holder_id, kind, body FROM key_grants WHERE key_owner = @o AND kind = @k;", conn);
        cmd.Parameters.AddWithValue("o", keyOwner);
        cmd.Parameters.AddWithValue("k", kind);
        return await ReadGrantsAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SaveGrantAsync(KeyGrant grant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        const string sql = @"
INSERT INTO key_grants (key_owner, holder_id, kind, body) VALUES (@o, @h, @k, @b)
ON CONFLICT (key_owner, holder_id, kind) DO UPDATE SET body = EXCLUDED.body, created_at = now();";
        await using NpgsqlConnection conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("o", grant.KeyOwner);
        cmd.Parameters.AddWithValue("h", grant.HolderId);
        cmd.Parameters.AddWithValue("k", grant.Kind);
        cmd.Parameters.AddWithValue("b", grant.Body);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteGrantAsync(Guid keyOwner, Guid holderId, string kind, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(kind);
        await using NpgsqlConnection conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("DELETE FROM key_grants WHERE key_owner = @o AND holder_id = @h AND kind = @k;", conn);
        cmd.Parameters.AddWithValue("o", keyOwner);
        cmd.Parameters.AddWithValue("h", holderId);
        cmd.Parameters.AddWithValue("k", kind);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SaveSessionKeysAsync(string sessionId, Guid mailboxId, byte[] body, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(body);
        const string sql = @"
INSERT INTO session_keys (session_id, mailbox_id, body, expires_at) VALUES (@s, @m, @b, @e)
ON CONFLICT (session_id) DO UPDATE SET mailbox_id = EXCLUDED.mailbox_id, body = EXCLUDED.body, expires_at = EXCLUDED.expires_at;";
        await using NpgsqlConnection conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("s", sessionId);
        cmd.Parameters.AddWithValue("m", mailboxId);
        cmd.Parameters.AddWithValue("b", body);
        cmd.Parameters.AddWithValue("e", expiresAt);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<byte[]?> GetSessionKeysAsync(string sessionId, Guid mailboxId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        await using NpgsqlConnection conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT body FROM session_keys WHERE session_id = @s AND mailbox_id = @m AND expires_at > now();", conn);
        cmd.Parameters.AddWithValue("s", sessionId);
        cmd.Parameters.AddWithValue("m", mailboxId);
        object? value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value as byte[];
    }

    /// <inheritdoc/>
    public async Task DeleteSessionKeysAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        await using NpgsqlConnection conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("DELETE FROM session_keys WHERE session_id = @s;", conn);
        cmd.Parameters.AddWithValue("s", sessionId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> DeleteMailboxSessionKeysAsync(Guid mailboxId, CancellationToken ct = default)
    {
        await using NpgsqlConnection conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("DELETE FROM session_keys WHERE mailbox_id = @m;", conn);
        cmd.Parameters.AddWithValue("m", mailboxId);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> PurgeSessionKeysAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using NpgsqlConnection conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("DELETE FROM session_keys WHERE expires_at <= @n;", conn);
        cmd.Parameters.AddWithValue("n", now);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<RestoreRequestRow?> GetRestoreRequestAsync(Guid mailboxId, CancellationToken ct = default)
    {
        await using NpgsqlConnection conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand($"SELECT {RestoreColumns} FROM restore_requests WHERE mailbox_id = @m;", conn);
        cmd.Parameters.AddWithValue("m", mailboxId);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? ReadRestore(r) : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RestoreRequestRow>> ListWaitingRestoresAsync(Guid tenantId, CancellationToken ct = default)
    {
        string sql = $"SELECT {string.Join(", ", RestoreColumns.Split(", ").Select(c => "r." + c))} FROM restore_requests r JOIN mailboxes m ON m.id = r.mailbox_id WHERE m.tenant_id = @t AND r.granted IS NULL ORDER BY r.created_at;";
        await using NpgsqlConnection conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        var result = new List<RestoreRequestRow>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadRestore(r));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task SaveRestoreRequestAsync(RestoreRequestRow row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        const string sql = @"
INSERT INTO restore_requests (mailbox_id, restore_public, restore_by_unlock, unlock_salt, granted, granted_by, granted_at, created_at)
VALUES (@m, @pub, @priv, @salt, @g, @by, @at, now())
ON CONFLICT (mailbox_id) DO UPDATE SET restore_public = EXCLUDED.restore_public, restore_by_unlock = EXCLUDED.restore_by_unlock,
    unlock_salt = EXCLUDED.unlock_salt, granted = EXCLUDED.granted, granted_by = EXCLUDED.granted_by, granted_at = EXCLUDED.granted_at;";
        await using NpgsqlConnection conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("m", row.MailboxId);
        cmd.Parameters.AddWithValue("pub", row.RestorePublic);
        cmd.Parameters.AddWithValue("priv", row.RestoreByUnlock);
        cmd.Parameters.AddWithValue("salt", row.UnlockSalt);
        AddBytes(cmd, "g", row.Granted);
        cmd.Parameters.AddWithValue("by", row.GrantedBy);
        cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = row.GrantedAt is DateTimeOffset a ? a : DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteRestoreRequestAsync(Guid mailboxId, CancellationToken ct = default)
    {
        await using NpgsqlConnection conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("DELETE FROM restore_requests WHERE mailbox_id = @m;", conn);
        cmd.Parameters.AddWithValue("m", mailboxId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BreakGlassRow?> GetBreakGlassAsync(CancellationToken ct = default)
    {
        await using NpgsqlConnection conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT public_key, private_by_secret, created_at FROM break_glass WHERE id = 1;", conn);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? new BreakGlassRow((byte[])r[0], (byte[])r[1], r.GetFieldValue<DateTimeOffset>(2)) : null;
    }

    /// <inheritdoc/>
    public async Task SaveBreakGlassAsync(BreakGlassRow row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        const string sql = @"
INSERT INTO break_glass (id, public_key, private_by_secret, created_at) VALUES (1, @pub, @priv, @at)
ON CONFLICT (id) DO UPDATE SET public_key = EXCLUDED.public_key, private_by_secret = EXCLUDED.private_by_secret, created_at = EXCLUDED.created_at;";
        await using NpgsqlConnection conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("pub", row.PublicKey);
        cmd.Parameters.AddWithValue("priv", row.PrivateBySecret);
        cmd.Parameters.AddWithValue("at", row.CreatedAt);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StoredMessageText>> ListStoredTextsAsync(Guid mailboxId, bool plain, int limit, CancellationToken ct = default)
    {
        // Plain: any of the texts not yet sealed (a plain text may itself begin "e1.", so the full marker is used).
        string where = plain ? "(subject NOT LIKE @marker OR body_text NOT LIKE @marker)" : "subject LIKE @arrival";
        await using NpgsqlConnection conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand($"SELECT id, folder_id, maildir_file, subject, body_text, snippet FROM messages WHERE mailbox_id = @m AND {where} ORDER BY received_at DESC LIMIT @limit;", conn);
        cmd.Parameters.AddWithValue("m", mailboxId);
        cmd.Parameters.AddWithValue("arrival", MessageCipher.ArrivalPrefix + "%");
        cmd.Parameters.AddWithValue("marker", MailEnvelope.FieldMarker + "%");
        cmd.Parameters.AddWithValue("limit", Math.Max(0, limit));
        var result = new List<StoredMessageText>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new StoredMessageText(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5)));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task SaveStoredTextAsync(Guid messageId, StoredText text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        await using NpgsqlConnection conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("UPDATE messages SET subject = @s, body_text = @b, snippet = @n WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", messageId);
        cmd.Parameters.AddWithValue("s", text.Subject);
        cmd.Parameters.AddWithValue("b", text.BodyText);
        cmd.Parameters.AddWithValue("n", text.Snippet);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>A mailbox's public key, or null when it has none (cached: a key, once made, never changes; "none" is checked again after a minute).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The key.</returns>
    internal async Task<byte[]?> MailboxPublicKeyAsync(Guid mailboxId, CancellationToken ct)
    {
        if (this.publicKeys.TryGetValue(mailboxId, out (byte[]? Key, DateTimeOffset At) cached) && (cached.Key is not null || DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(1)))
        {
            return cached.Key;
        }
        IReadOnlyDictionary<Guid, byte[]> found = await this.GetPublicKeysAsync(new[] { mailboxId }, ct).ConfigureAwait(false);
        byte[]? key = found.TryGetValue(mailboxId, out byte[]? k) ? k : null;
        this.publicKeys[mailboxId] = (key, DateTimeOffset.UtcNow);
        return key;
    }

    /// <inheritdoc/>
    public async Task ReplaceOutboundRawAsync(Guid id, byte[] raw, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(raw);
        await using NpgsqlConnection conn = await this.OpenAsync(ct).ConfigureAwait(false);
        string from;
        await using (var get = new NpgsqlCommand("SELECT envelope_from FROM outbound_messages WHERE id = @id;", conn))
        {
            get.Parameters.AddWithValue("id", id);
            from = await get.ExecuteScalarAsync(ct).ConfigureAwait(false) as string ?? string.Empty;
        }
        byte[] stored = OutboundCipher.Seal(raw, await this.QueueSealKeyAsync(ct).ConfigureAwait(false), await this.RecordKeyAsync(from, ct).ConfigureAwait(false));
        await using var cmd = new NpgsqlCommand("UPDATE outbound_messages SET raw_bytes = @raw WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("raw", stored);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The mail server's seal public key for the outgoing queue (DES-15 §7.4), or null before the server has made it.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The key.</returns>
    internal async Task<byte[]?> QueueSealKeyAsync(CancellationToken ct)
    {
        if (this.queueSeal.Key is not null || DateTimeOffset.UtcNow - this.queueSeal.At < TimeSpan.FromMinutes(1))
        {
            return this.queueSeal.Key;
        }
        SealRecord? record = await KeySeal.ReadRecordAsync(this, ct).ConfigureAwait(false);
        byte[]? key = null;
        if (record is not null && record.PublicKey.Length > 0)
        {
            try
            {
                key = Convert.FromBase64String(record.PublicKey);
            }
            catch (FormatException)
            {
                key = null;
            }
        }
        this.queueSeal = (key, DateTimeOffset.UtcNow);
        return key;
    }

    /// <summary>
    /// Who may read a queued message's record (DES-15 §7.4): the sending mailbox's key, else the
    /// organisation's recovery key for the sending domain (an application's mail), else nobody in
    /// particular (null - the record stays plain, as for an individual's mail).
    /// </summary>
    /// <param name="envelopeFrom">The sender.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The public key, or null.</returns>
    internal async Task<byte[]?> RecordKeyAsync(string envelopeFrom, CancellationToken ct)
    {
        int at = envelopeFrom.LastIndexOf('@');
        if (at <= 0 || at == envelopeFrom.Length - 1)
        {
            return null;
        }
        string local = envelopeFrom[..at];
        string domain = envelopeFrom[(at + 1)..];
        if (await this.GetMailboxAsync(local, domain, ct).ConfigureAwait(false) is MailboxRow box &&
            await this.MailboxPublicKeyAsync(box.Id, ct).ConfigureAwait(false) is byte[] mailboxKey)
        {
            return mailboxKey;
        }
        TenantDomainRow? owner = await this.GetTenantDomainAsync(domain, ct).ConfigureAwait(false);
        return owner is null ? null : (await this.GetTenantKeysAsync(owner.TenantId, ct).ConfigureAwait(false))?.RecoveryPublic;
    }

    // DES-15 §8: an encrypted mailbox is searched in the session - its rows opened with the person's keys.
    private async Task<List<MessageRow>> SearchInSessionAsync(Guid mailboxId, Guid? folderId, string query, CancellationToken ct)
    {
        const string sql = "SELECT " + MessageColumns + ", body_text FROM messages WHERE mailbox_id = @mailbox_id AND (@folder_id IS NULL OR folder_id = @folder_id) ORDER BY received_at DESC, id DESC LIMIT 20000;";
        await using NpgsqlConnection conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("mailbox_id", mailboxId);
        cmd.Parameters.Add(new NpgsqlParameter<Guid?>("folder_id", NpgsqlDbType.Uuid) { TypedValue = folderId });
        var result = new List<MessageRow>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            MessageRow row = ReadMailboxMessage(r);
            if (MessageCipher.Matches(row, MessageCipher.Open(r.GetString(23)), query))
            {
                result.Add(row);
            }
        }
        return result;
    }

    private static void AddBytes(NpgsqlCommand cmd, string name, byte[]? value) =>
        cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Bytea) { Value = value is null ? DBNull.Value : value });

    private static MailboxKeyRow ReadMailboxKeys(NpgsqlDataReader r) => new()
    {
        MailboxId = r.GetGuid(0),
        PublicKey = (byte[])r[1],
        UnlockSalt = (byte[])r[2],
        PrivateByPassword = r.IsDBNull(3) ? null : (byte[])r[3],
        PrivateByOrg = (byte[])r[4],
        DataKey = (byte[])r[5],
        CreatedAt = r.GetFieldValue<DateTimeOffset>(6),
        UpdatedAt = r.GetFieldValue<DateTimeOffset>(7),
    };

    private static RestoreRequestRow ReadRestore(NpgsqlDataReader r) => new()
    {
        MailboxId = r.GetGuid(0),
        RestorePublic = (byte[])r[1],
        RestoreByUnlock = (byte[])r[2],
        UnlockSalt = (byte[])r[3],
        Granted = r.IsDBNull(4) ? null : (byte[])r[4],
        GrantedBy = r.GetString(5),
        GrantedAt = r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
        CreatedAt = r.GetFieldValue<DateTimeOffset>(7),
    };

    private static async Task<IReadOnlyList<KeyGrant>> ReadGrantsAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var result = new List<KeyGrant>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new KeyGrant(r.GetGuid(0), r.GetGuid(1), r.GetString(2), (byte[])r[3]));
        }
        return result;
    }
}
