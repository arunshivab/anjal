using Npgsql;

namespace Anjal.Store;

/// <summary>rc.28 additions to the PostgreSQL store: secure links (DES-19) - see <c>tools/sql/schema.sql</c>.</summary>
public sealed partial class PostgresMessageStore : ISecureLinkStore
{
    private const string SecureLinkColumns = "id, mailbox_id, sent_by, recipient, token_sha256, sealed, message_id, sent_copy_id, created_at, expires_at, stopped_at, locked_at, mailed_at, last_code_at, opened_at, replied_at, wrong_codes, replies, purged_at";

    /// <inheritdoc/>
    public async Task<SecureLinkRow> AddSecureLinkAsync(SecureLinkRow link, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(link);
        const string sql = "INSERT INTO secure_links (id, mailbox_id, sent_by, recipient, token_sha256, sealed, message_id, sent_copy_id, created_at, expires_at) "
            + "VALUES (@id, @m, @by, @r, @t, @s, @mid, @copy, @c, @e) RETURNING " + SecureLinkColumns;
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", link.Id == System.Guid.Empty ? System.Guid.NewGuid() : link.Id);
        cmd.Parameters.AddWithValue("m", link.MailboxId);
        cmd.Parameters.Add(new NpgsqlParameter<System.Guid?>("by", NpgsqlTypes.NpgsqlDbType.Uuid) { TypedValue = link.SentBy });
        cmd.Parameters.AddWithValue("r", link.Recipient);
        cmd.Parameters.AddWithValue("t", link.TokenSha256);
        cmd.Parameters.AddWithValue("s", link.Sealed);
        cmd.Parameters.AddWithValue("mid", link.MessageId);
        cmd.Parameters.Add(new NpgsqlParameter<System.Guid?>("copy", NpgsqlTypes.NpgsqlDbType.Uuid) { TypedValue = link.SentCopyId });
        cmd.Parameters.AddWithValue("c", link.CreatedAt.ToUniversalTime());
        cmd.Parameters.AddWithValue("e", link.ExpiresAt.ToUniversalTime());
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new System.InvalidOperationException("The secure link was not kept.");
        }
        return ReadSecureLink(reader);
    }

    /// <inheritdoc/>
    public async Task<SecureLinkRow?> GetSecureLinkByTokenAsync(string tokenSha256, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(tokenSha256);
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT " + SecureLinkColumns + " FROM secure_links WHERE token_sha256 = @t", conn);
        cmd.Parameters.AddWithValue("t", tokenSha256);
        return await ReadOneLinkAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<SecureLinkRow?> GetSecureLinkAsync(System.Guid id, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT " + SecureLinkColumns + " FROM secure_links WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        return await ReadOneLinkAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SecureLinkRow>> ListSecureLinksForMessageAsync(System.Guid mailboxId, string messageId, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(messageId);
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT " + SecureLinkColumns + " FROM secure_links WHERE mailbox_id = @m AND message_id = @mid ORDER BY created_at, recipient", conn);
        cmd.Parameters.AddWithValue("m", mailboxId);
        cmd.Parameters.AddWithValue("mid", messageId);
        var list = new List<SecureLinkRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadSecureLink(reader));
        }
        return list;
    }

    /// <inheritdoc/>
    public async Task<SecureLinkRow?> UpdateSecureLinkAsync(System.Guid id, SecureLinkChange change, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(change);
        const string sql = @"UPDATE secure_links SET
    stopped_at = COALESCE(stopped_at, @stopped),
    locked_at = COALESCE(locked_at, @locked),
    mailed_at = COALESCE(@mailed, mailed_at),
    last_code_at = COALESCE(@code, last_code_at),
    opened_at = COALESCE(opened_at, @opened),
    replied_at = COALESCE(@replied, replied_at),
    wrong_codes = wrong_codes + @wrong,
    replies = replies + @rep
WHERE id = @id RETURNING " + SecureLinkColumns;
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        AddTime(cmd, "stopped", change.StoppedAt);
        AddTime(cmd, "locked", change.LockedAt);
        AddTime(cmd, "mailed", change.MailedAt);
        AddTime(cmd, "code", change.LastCodeAt);
        AddTime(cmd, "opened", change.OpenedAt);
        AddTime(cmd, "replied", change.RepliedAt);
        cmd.Parameters.AddWithValue("wrong", change.AddWrongCodes);
        cmd.Parameters.AddWithValue("rep", change.AddReplies);
        return await ReadOneLinkAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int?> TakeSecureCodeTryAsync(System.Guid id, int most, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("UPDATE secure_links SET wrong_codes = wrong_codes + 1 WHERE id = @id AND wrong_codes < @most AND locked_at IS NULL AND stopped_at IS NULL RETURNING wrong_codes", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("most", most);
        object? n = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return n is int count ? count : null;
    }

    /// <inheritdoc/>
    public async Task<bool> TakeSecureReplyAsync(System.Guid id, System.DateTimeOffset visitStart, System.DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("UPDATE secure_links SET replied_at = @now WHERE id = @id AND (replied_at IS NULL OR replied_at < @visit)", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("now", now.ToUniversalTime());
        cmd.Parameters.AddWithValue("visit", visitStart.ToUniversalTime());
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task<bool> TakeSecureCodeMailAsync(System.Guid id, System.DateTimeOffset now, System.DateTimeOffset gapStart, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("UPDATE secure_links SET last_code_at = @now WHERE id = @id AND (last_code_at IS NULL OR last_code_at < @gap)", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("now", now.ToUniversalTime());
        cmd.Parameters.AddWithValue("gap", gapStart.ToUniversalTime());
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task AddSecureLinkCodeAsync(SecureLinkCodeRow code, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(code);
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("INSERT INTO secure_link_codes (id, link_id, code_hmac, made_at) VALUES (@id, @l, @h, @at)", conn);
        cmd.Parameters.AddWithValue("id", code.Id == System.Guid.Empty ? System.Guid.NewGuid() : code.Id);
        cmd.Parameters.AddWithValue("l", code.LinkId);
        cmd.Parameters.AddWithValue("h", code.CodeHmac);
        cmd.Parameters.AddWithValue("at", code.MadeAt.ToUniversalTime());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SecureLinkCodeRow>> ListSecureLinkCodesAsync(System.Guid linkId, System.DateTimeOffset since, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT id, link_id, code_hmac, made_at, used_at FROM secure_link_codes WHERE link_id = @l AND made_at >= @s ORDER BY made_at DESC", conn);
        cmd.Parameters.AddWithValue("l", linkId);
        cmd.Parameters.AddWithValue("s", since.ToUniversalTime());
        var list = new List<SecureLinkCodeRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new SecureLinkCodeRow
            {
                Id = r.GetGuid(0),
                LinkId = r.GetGuid(1),
                CodeHmac = r.GetString(2),
                MadeAt = r.GetFieldValue<System.DateTimeOffset>(3),
                UsedAt = r.IsDBNull(4) ? null : r.GetFieldValue<System.DateTimeOffset>(4),
            });
        }
        return list;
    }

    /// <inheritdoc/>
    public async Task<bool> UseSecureLinkCodeAsync(System.Guid codeId, System.DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("UPDATE secure_link_codes SET used_at = @at WHERE id = @id AND used_at IS NULL", conn);
        cmd.Parameters.AddWithValue("id", codeId);
        cmd.Parameters.AddWithValue("at", at.ToUniversalTime());
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task<int> PurgeEndedSecureLinksAsync(System.DateTimeOffset endedBefore, System.DateTimeOffset now, CancellationToken ct = default)
    {
        const string sql = @"
WITH ended AS (
    UPDATE secure_links SET sealed = ''::bytea, purged_at = @now
    WHERE purged_at IS NULL AND COALESCE(stopped_at, locked_at, expires_at) < @before
    RETURNING id),
gone AS (DELETE FROM secure_link_codes WHERE link_id IN (SELECT id FROM ended))
SELECT count(*) FROM ended;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("before", endedBefore.ToUniversalTime());
        cmd.Parameters.AddWithValue("now", now.ToUniversalTime());
        object? n = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return n is long count ? (int)count : 0;
    }

    private static void AddTime(NpgsqlCommand cmd, string name, System.DateTimeOffset? value) =>
        cmd.Parameters.Add(new NpgsqlParameter<System.DateTimeOffset?>(name, NpgsqlTypes.NpgsqlDbType.TimestampTz) { TypedValue = value?.ToUniversalTime() });

    private static async Task<SecureLinkRow?> ReadOneLinkAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadSecureLink(reader) : null;
    }

    private static SecureLinkRow ReadSecureLink(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        MailboxId = r.GetGuid(1),
        SentBy = r.IsDBNull(2) ? null : r.GetGuid(2),
        Recipient = r.GetString(3),
        TokenSha256 = r.GetString(4),
        Sealed = r.GetFieldValue<byte[]>(5),
        MessageId = r.GetString(6),
        SentCopyId = r.IsDBNull(7) ? null : r.GetGuid(7),
        CreatedAt = r.GetFieldValue<System.DateTimeOffset>(8),
        ExpiresAt = r.GetFieldValue<System.DateTimeOffset>(9),
        StoppedAt = r.IsDBNull(10) ? null : r.GetFieldValue<System.DateTimeOffset>(10),
        LockedAt = r.IsDBNull(11) ? null : r.GetFieldValue<System.DateTimeOffset>(11),
        MailedAt = r.IsDBNull(12) ? null : r.GetFieldValue<System.DateTimeOffset>(12),
        LastCodeAt = r.IsDBNull(13) ? null : r.GetFieldValue<System.DateTimeOffset>(13),
        OpenedAt = r.IsDBNull(14) ? null : r.GetFieldValue<System.DateTimeOffset>(14),
        RepliedAt = r.IsDBNull(15) ? null : r.GetFieldValue<System.DateTimeOffset>(15),
        WrongCodes = r.GetInt32(16),
        Replies = r.GetInt32(17),
        PurgedAt = r.IsDBNull(18) ? null : r.GetFieldValue<System.DateTimeOffset>(18),
    };
}
