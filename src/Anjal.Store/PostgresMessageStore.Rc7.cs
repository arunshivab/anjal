using Npgsql;

namespace Anjal.Store;

/// <summary>
/// v1.0.0-rc.7 additions to the PostgreSQL store: trusted senders,
/// greylisting memory and settings. Tables: <c>mailbox_trusted_senders</c>,
/// <c>greylist_entries</c>, <c>settings</c> - see <c>tools/sql/schema.sql</c>.
/// </summary>
public sealed partial class PostgresMessageStore
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> ListTrustedSendersAsync(System.Guid mailboxId, CancellationToken ct = default)
    {
        const string sql = "SELECT address FROM mailbox_trusted_senders WHERE mailbox_id = @mailbox_id ORDER BY address;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("mailbox_id", mailboxId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<string>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task AddTrustedSenderAsync(System.Guid mailboxId, string address, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(address);
        const string sql = "INSERT INTO mailbox_trusted_senders (mailbox_id, address) VALUES (@mailbox_id, lower(@address)) ON CONFLICT DO NOTHING;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("mailbox_id", mailboxId);
        cmd.Parameters.AddWithValue("address", address.Trim());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveTrustedSenderAsync(System.Guid mailboxId, string address, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(address);
        const string sql = "DELETE FROM mailbox_trusted_senders WHERE mailbox_id = @mailbox_id AND address = lower(@address);";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("mailbox_id", mailboxId);
        cmd.Parameters.AddWithValue("address", address.Trim());
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<GreylistRow>> ListGreylistAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT key, first_seen, last_seen, passed FROM greylist_entries;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<GreylistRow>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new GreylistRow
            {
                Key = reader.GetString(0),
                FirstSeen = reader.GetFieldValue<System.DateTimeOffset>(1),
                LastSeen = reader.GetFieldValue<System.DateTimeOffset>(2),
                Passed = reader.GetBoolean(3),
            });
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task ReplaceGreylistAsync(IReadOnlyList<GreylistRow> rows, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(rows);
        var keys = new string[rows.Count];
        var first = new System.DateTimeOffset[rows.Count];
        var last = new System.DateTimeOffset[rows.Count];
        var passed = new bool[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            keys[i] = rows[i].Key;
            first[i] = rows[i].FirstSeen.ToUniversalTime();
            last[i] = rows[i].LastSeen.ToUniversalTime();
            passed[i] = rows[i].Passed;
        }
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var del = new NpgsqlCommand("DELETE FROM greylist_entries;", conn, tx))
        {
            await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        if (rows.Count > 0)
        {
            const string sql = "INSERT INTO greylist_entries (key, first_seen, last_seen, passed) SELECT * FROM unnest(@k, @f, @l, @p) ON CONFLICT (key) DO NOTHING;";
            await using var ins = new NpgsqlCommand(sql, conn, tx);
            ins.Parameters.AddWithValue("k", keys);
            ins.Parameters.AddWithValue("f", first);
            ins.Parameters.AddWithValue("l", last);
            ins.Parameters.AddWithValue("p", passed);
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SettingRow>> ListSettingsAsync(string scope, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(scope);
        const string sql = "SELECT scope, key, value, updated_at, updated_by FROM settings WHERE scope = @scope ORDER BY key;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("scope", scope);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<SettingRow>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadSetting(reader));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<SettingRow> UpsertSettingAsync(SettingRow setting, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(setting);
        const string sql = @"
INSERT INTO settings (scope, key, value, updated_at, updated_by)
VALUES (@scope, @key, @value, now(), @updated_by)
ON CONFLICT (scope, key) DO UPDATE
    SET value = EXCLUDED.value, updated_at = now(), updated_by = EXCLUDED.updated_by
RETURNING scope, key, value, updated_at, updated_by;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("scope", setting.Scope);
        cmd.Parameters.AddWithValue("key", setting.Key);
        cmd.Parameters.AddWithValue("value", setting.Value);
        cmd.Parameters.AddWithValue("updated_by", setting.UpdatedBy);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return ReadSetting(reader);
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteSettingAsync(string scope, string key, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(scope);
        System.ArgumentNullException.ThrowIfNull(key);
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("DELETE FROM settings WHERE scope = @scope AND key = @key;", conn);
        cmd.Parameters.AddWithValue("scope", scope);
        cmd.Parameters.AddWithValue("key", key);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<int> ImportSettingsAsync(IReadOnlyList<SettingRow> rows, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(rows);
        int inserted = 0;
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (SettingRow row in rows)
        {
            const string sql = "INSERT INTO settings (scope, key, value, updated_by) VALUES (@scope, @key, @value, @updated_by) ON CONFLICT (scope, key) DO NOTHING;";
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("scope", row.Scope);
            cmd.Parameters.AddWithValue("key", row.Key);
            cmd.Parameters.AddWithValue("value", row.Value);
            cmd.Parameters.AddWithValue("updated_by", row.UpdatedBy);
            inserted += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return inserted;
    }

    private static SettingRow ReadSetting(NpgsqlDataReader r) => new()
    {
        Scope = r.GetString(0),
        Key = r.GetString(1),
        Value = r.GetString(2),
        UpdatedAt = r.GetFieldValue<System.DateTimeOffset>(3),
        UpdatedBy = r.GetString(4),
    };
}
