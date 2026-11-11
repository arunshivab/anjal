using Npgsql;

namespace Anjal.Store;

/// <summary>rc.28 additions to the PostgreSQL store: app passwords, IMAP numbers and import jobs (DES-20) - see <c>tools/sql/schema.sql</c>.</summary>
public sealed partial class PostgresMessageStore : IMailAppStore
{
    private const string AppPasswordColumns = "id, mailbox_id, name, hash, unlock_salt, private_by_app, created_at, last_used_at, last_used_from, removed_at";
    private const string ImportJobColumns = "id, mailbox_id, kind, source, secret, target_folder, folders, cursor, state, seen, imported, skipped, total, error, created_at, updated_at";

    /// <inheritdoc/>
    public async Task<AppPasswordRow> AddAppPasswordAsync(AppPasswordRow row, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(row);
        const string sql = "INSERT INTO app_passwords (id, mailbox_id, name, hash, unlock_salt, private_by_app, created_at) VALUES (@id, @m, @n, @h, @s, @p, @c) RETURNING " + AppPasswordColumns;
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", row.Id == System.Guid.Empty ? System.Guid.NewGuid() : row.Id);
        cmd.Parameters.AddWithValue("m", row.MailboxId);
        cmd.Parameters.AddWithValue("n", row.Name);
        cmd.Parameters.AddWithValue("h", row.Hash);
        cmd.Parameters.AddWithValue("s", row.UnlockSalt);
        cmd.Parameters.Add(new NpgsqlParameter<byte[]?>("p", NpgsqlTypes.NpgsqlDbType.Bytea) { TypedValue = row.PrivateByApp });
        cmd.Parameters.AddWithValue("c", row.CreatedAt.ToUniversalTime());
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new System.InvalidOperationException("The app password was not kept.");
        }
        return ReadAppPassword(reader);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AppPasswordRow>> ListAppPasswordsAsync(System.Guid mailboxId, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT " + AppPasswordColumns + " FROM app_passwords WHERE mailbox_id = @m AND removed_at IS NULL ORDER BY created_at DESC", conn);
        cmd.Parameters.AddWithValue("m", mailboxId);
        var list = new List<AppPasswordRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadAppPassword(reader));
        }
        return list;
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAppPasswordAsync(System.Guid mailboxId, System.Guid id, System.DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("UPDATE app_passwords SET removed_at = @at, private_by_app = NULL, hash = '' WHERE id = @id AND mailbox_id = @m AND removed_at IS NULL", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("m", mailboxId);
        cmd.Parameters.AddWithValue("at", at.ToUniversalTime());
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task TouchAppPasswordAsync(System.Guid id, System.DateTimeOffset at, string from, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(from);
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("UPDATE app_passwords SET last_used_at = @at, last_used_from = @f WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("at", at.ToUniversalTime());
        cmd.Parameters.AddWithValue("f", from);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<ImapFolderNumbers> AssignImapUidsAsync(System.Guid folderId, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        // The folder's row is locked: two sessions giving numbers at once give each message one UID.
        const string sql = @"
INSERT INTO imap_folders (folder_id, uid_validity) VALUES (@f, @v) ON CONFLICT (folder_id) DO NOTHING;
SELECT uid_validity, uid_next FROM imap_folders WHERE folder_id = @f FOR UPDATE;";
        long validity;
        long next;
        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
        {
            cmd.Parameters.AddWithValue("f", folderId);
            cmd.Parameters.AddWithValue("v", System.DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await r.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new System.InvalidOperationException("The folder's IMAP numbers were not kept.");
            }
            validity = r.GetInt64(0);
            next = r.GetInt64(1);
        }
        const string give = @"
WITH need AS (
    SELECT m.id, row_number() OVER (ORDER BY m.received_at, m.id) AS rn
    FROM messages m LEFT JOIN imap_uids u ON u.message_id = m.id AND u.folder_id = @f
    WHERE m.folder_id = @f AND u.message_id IS NULL),
given AS (
    INSERT INTO imap_uids (message_id, folder_id, uid) SELECT id, @f, @next + rn - 1 FROM need
    ON CONFLICT (message_id) DO UPDATE SET folder_id = EXCLUDED.folder_id, uid = EXCLUDED.uid
    RETURNING 1)
SELECT count(*) FROM given;";
        long added;
        await using (var cmd = new NpgsqlCommand(give, conn, tx))
        {
            cmd.Parameters.AddWithValue("f", folderId);
            cmd.Parameters.AddWithValue("next", next);
            added = (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        }
        if (added > 0)
        {
            next += added;
            await using var up = new NpgsqlCommand("UPDATE imap_folders SET uid_next = @n WHERE folder_id = @f", conn, tx);
            up.Parameters.AddWithValue("f", folderId);
            up.Parameters.AddWithValue("n", next);
            await up.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        var uids = new List<(System.Guid, long)>();
        await using (var cmd = new NpgsqlCommand("SELECT m.id, u.uid FROM messages m JOIN imap_uids u ON u.message_id = m.id AND u.folder_id = @f WHERE m.folder_id = @f ORDER BY u.uid", conn, tx))
        {
            cmd.Parameters.AddWithValue("f", folderId);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                uids.Add((r.GetGuid(0), r.GetInt64(1)));
            }
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new ImapFolderNumbers(validity, next, uids);
    }

    /// <inheritdoc/>
    public async Task SetReceivedAtAsync(System.Guid messageId, System.DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("UPDATE messages SET received_at = @at WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", messageId);
        cmd.Parameters.AddWithValue("at", at.ToUniversalTime());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<ImportJobRow> AddImportJobAsync(ImportJobRow job, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(job);
        const string sql = "INSERT INTO import_jobs (id, mailbox_id, kind, source, secret, target_folder, folders, state, created_at, updated_at) "
            + "VALUES (@id, @m, @k, @s, @sec, @t, @fo, @st, @c, @c) RETURNING " + ImportJobColumns;
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", job.Id == System.Guid.Empty ? System.Guid.NewGuid() : job.Id);
        cmd.Parameters.AddWithValue("m", job.MailboxId);
        cmd.Parameters.AddWithValue("k", job.Kind);
        cmd.Parameters.AddWithValue("s", job.Source);
        cmd.Parameters.AddWithValue("sec", job.Secret);
        cmd.Parameters.AddWithValue("t", job.TargetFolder);
        cmd.Parameters.AddWithValue("fo", job.Folders);
        cmd.Parameters.AddWithValue("st", job.State);
        cmd.Parameters.AddWithValue("c", job.CreatedAt.ToUniversalTime());
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new System.InvalidOperationException("The import job was not kept.");
        }
        return ReadImportJob(reader);
    }

    /// <inheritdoc/>
    public async Task<ImportJobRow?> GetImportJobAsync(System.Guid id, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT " + ImportJobColumns + " FROM import_jobs WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadImportJob(reader) : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ImportJobRow>> ListImportJobsAsync(System.Guid mailboxId, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT " + ImportJobColumns + " FROM import_jobs WHERE mailbox_id = @m ORDER BY created_at DESC", conn);
        cmd.Parameters.AddWithValue("m", mailboxId);
        return await ReadImportJobsAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ImportJobRow>> ListOpenImportJobsAsync(CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT " + ImportJobColumns + " FROM import_jobs WHERE state IN ('waiting', 'running') ORDER BY created_at", conn);
        return await ReadImportJobsAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> ExpireChoosingImportJobsAsync(System.DateTimeOffset before, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("UPDATE import_jobs SET state = 'stopped', secret = '', updated_at = now() WHERE state = 'choosing' AND created_at < @before", conn);
        cmd.Parameters.AddWithValue("before", before.ToUniversalTime());
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> ClaimImportJobAsync(System.Guid id, System.DateTimeOffset staleBefore, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("UPDATE import_jobs SET state = 'running', updated_at = now() WHERE id = @id AND (state = 'waiting' OR (state = 'running' AND updated_at < @stale))", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("stale", staleBefore.ToUniversalTime());
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task SaveImportJobAsync(ImportJobRow job, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(job);
        const string sql = @"UPDATE import_jobs SET secret = @sec, cursor = @cur, state = @st, seen = @seen, imported = @imp, skipped = @skip,
    total = @tot, error = @err, folders = @fo, updated_at = now() WHERE id = @id AND (state <> 'stopped' OR @st = 'stopped')";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", job.Id);
        cmd.Parameters.AddWithValue("sec", job.Secret);
        cmd.Parameters.AddWithValue("cur", job.Cursor);
        cmd.Parameters.AddWithValue("st", job.State);
        cmd.Parameters.AddWithValue("seen", job.Seen);
        cmd.Parameters.AddWithValue("imp", job.Imported);
        cmd.Parameters.AddWithValue("skip", job.Skipped);
        cmd.Parameters.AddWithValue("tot", job.Total);
        cmd.Parameters.AddWithValue("err", job.Error);
        cmd.Parameters.AddWithValue("fo", job.Folders);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ImportJobRow>> ReadImportJobsAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<ImportJobRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadImportJob(reader));
        }
        return list;
    }

    private static AppPasswordRow ReadAppPassword(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        MailboxId = r.GetGuid(1),
        Name = r.GetString(2),
        Hash = r.GetString(3),
        UnlockSalt = r.GetFieldValue<byte[]>(4),
        PrivateByApp = r.IsDBNull(5) ? null : r.GetFieldValue<byte[]>(5),
        CreatedAt = r.GetFieldValue<System.DateTimeOffset>(6),
        LastUsedAt = r.IsDBNull(7) ? null : r.GetFieldValue<System.DateTimeOffset>(7),
        LastUsedFrom = r.GetString(8),
        RemovedAt = r.IsDBNull(9) ? null : r.GetFieldValue<System.DateTimeOffset>(9),
    };

    private static ImportJobRow ReadImportJob(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        MailboxId = r.GetGuid(1),
        Kind = r.GetString(2),
        Source = r.GetString(3),
        Secret = r.GetString(4),
        TargetFolder = r.GetString(5),
        Folders = r.GetString(6),
        Cursor = r.GetString(7),
        State = r.GetString(8),
        Seen = r.GetInt32(9),
        Imported = r.GetInt32(10),
        Skipped = r.GetInt32(11),
        Total = r.GetInt32(12),
        Error = r.GetString(13),
        CreatedAt = r.GetFieldValue<System.DateTimeOffset>(14),
        UpdatedAt = r.GetFieldValue<System.DateTimeOffset>(15),
    };
}
