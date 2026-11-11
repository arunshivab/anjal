using Npgsql;

namespace Anjal.Store;

/// <summary>rc.28 additions to the PostgreSQL store: chat and online status (DES-21) - see <c>tools/sql/schema.sql</c>.</summary>
public sealed partial class PostgresMessageStore : IChatStore
{
    private const string RoomColumns = "id, tenant_id, name, is_group, created_by, created_at, last_message_at";
    private const string ChatColumns = "id, room_id, sender_id, body, created_at, removed_at";

    /// <inheritdoc/>
    public async Task<ChatRoomRow> AddChatRoomAsync(ChatRoomRow room, IReadOnlyCollection<System.Guid> members, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(room);
        System.ArgumentNullException.ThrowIfNull(members);
        System.Guid id = room.Id == System.Guid.Empty ? System.Guid.NewGuid() : room.Id;
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var cmd = new NpgsqlCommand("INSERT INTO chat_rooms (id, tenant_id, name, is_group, created_by, created_at, last_message_at) VALUES (@id, @t, @n, @g, @by, @at, @at)", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("t", room.TenantId);
            cmd.Parameters.AddWithValue("n", room.Name);
            cmd.Parameters.AddWithValue("g", room.IsGroup);
            cmd.Parameters.AddWithValue("by", room.CreatedBy);
            cmd.Parameters.AddWithValue("at", room.CreatedAt.ToUniversalTime());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        foreach (System.Guid m in members.Distinct())
        {
            await using var add = new NpgsqlCommand("INSERT INTO chat_members (room_id, mailbox_id, joined_at, last_read_at) VALUES (@r, @m, @at, @at) ON CONFLICT DO NOTHING", conn, tx);
            add.Parameters.AddWithValue("r", id);
            add.Parameters.AddWithValue("m", m);
            add.Parameters.AddWithValue("at", room.CreatedAt.ToUniversalTime());
            await add.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return (await this.GetChatRoomAsync(id, ct).ConfigureAwait(false))!;
    }

    /// <inheritdoc/>
    public async Task<ChatRoomRow?> GetChatRoomAsync(System.Guid id, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT " + RoomColumns + " FROM chat_rooms WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? ReadRoom(r) : null;
    }

    /// <inheritdoc/>
    public async Task<ChatRoomRow?> FindDirectChatAsync(System.Guid a, System.Guid b, CancellationToken ct = default)
    {
        const string sql = @"SELECT " + RoomColumns + @" FROM chat_rooms c WHERE NOT c.is_group
    AND EXISTS (SELECT 1 FROM chat_members m WHERE m.room_id = c.id AND m.mailbox_id = @a)
    AND EXISTS (SELECT 1 FROM chat_members m WHERE m.room_id = c.id AND m.mailbox_id = @b)
    AND (SELECT count(*) FROM chat_members m WHERE m.room_id = c.id) = @n
ORDER BY c.created_at LIMIT 1";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("a", a);
        cmd.Parameters.AddWithValue("b", b);
        cmd.Parameters.AddWithValue("n", a == b ? 1L : 2L);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? ReadRoom(r) : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ChatRoomRow>> ListChatRoomsAsync(System.Guid mailboxId, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT " + string.Join(", ", RoomColumns.Split(", ").Select(c => "c." + c)) + " FROM chat_rooms c JOIN chat_members m ON m.room_id = c.id WHERE m.mailbox_id = @m AND m.left_at IS NULL ORDER BY c.last_message_at DESC", conn);
        cmd.Parameters.AddWithValue("m", mailboxId);
        var list = new List<ChatRoomRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadRoom(r));
        }
        return list;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ChatMemberRow>> ListChatMembersAsync(System.Guid roomId, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT room_id, mailbox_id, joined_at, left_at, last_read_at FROM chat_members WHERE room_id = @r", conn);
        cmd.Parameters.AddWithValue("r", roomId);
        var list = new List<ChatMemberRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ChatMemberRow
            {
                RoomId = r.GetGuid(0),
                MailboxId = r.GetGuid(1),
                JoinedAt = r.GetFieldValue<System.DateTimeOffset>(2),
                LeftAt = r.IsDBNull(3) ? null : r.GetFieldValue<System.DateTimeOffset>(3),
                LastReadAt = r.GetFieldValue<System.DateTimeOffset>(4),
            });
        }
        return list;
    }

    /// <inheritdoc/>
    public async Task AddChatMemberAsync(System.Guid roomId, System.Guid mailboxId, System.DateTimeOffset at, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO chat_members (room_id, mailbox_id, joined_at, last_read_at) VALUES (@r, @m, @at, @at)
ON CONFLICT (room_id, mailbox_id) DO UPDATE SET left_at = NULL,
    joined_at = CASE WHEN chat_members.left_at IS NULL THEN chat_members.joined_at ELSE EXCLUDED.joined_at END,
    last_read_at = CASE WHEN chat_members.left_at IS NULL THEN chat_members.last_read_at ELSE EXCLUDED.last_read_at END";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("r", roomId);
        cmd.Parameters.AddWithValue("m", mailboxId);
        cmd.Parameters.AddWithValue("at", at.ToUniversalTime());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task LeaveChatAsync(System.Guid roomId, System.Guid mailboxId, System.DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("UPDATE chat_members SET left_at = @at WHERE room_id = @r AND mailbox_id = @m AND left_at IS NULL", conn);
        cmd.Parameters.AddWithValue("r", roomId);
        cmd.Parameters.AddWithValue("m", mailboxId);
        cmd.Parameters.AddWithValue("at", at.ToUniversalTime());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<ChatMessageRow> AddChatMessageAsync(ChatMessageRow message, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(message);
        const string sql = @"
WITH added AS (INSERT INTO chat_messages (id, room_id, sender_id, body, created_at) VALUES (@id, @r, @s, @b, @at) RETURNING " + ChatColumns + @"),
moved AS (UPDATE chat_rooms SET last_message_at = GREATEST(last_message_at, @at) WHERE id = @r)
SELECT " + ChatColumns + " FROM added";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", message.Id == System.Guid.Empty ? System.Guid.NewGuid() : message.Id);
        cmd.Parameters.AddWithValue("r", message.RoomId);
        cmd.Parameters.AddWithValue("s", message.SenderId);
        cmd.Parameters.AddWithValue("b", message.Body);
        cmd.Parameters.AddWithValue("at", message.CreatedAt.ToUniversalTime());
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new System.InvalidOperationException("The chat message was not kept.");
        }
        return ReadChat(r);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ChatMessageRow>> ListChatMessagesAsync(System.Guid roomId, System.DateTimeOffset after, int limit, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT * FROM (SELECT " + ChatColumns + " FROM chat_messages WHERE room_id = @r AND created_at > @after ORDER BY created_at DESC, id DESC LIMIT @l) x ORDER BY created_at, id", conn);
        cmd.Parameters.AddWithValue("r", roomId);
        cmd.Parameters.AddWithValue("after", after.ToUniversalTime());
        cmd.Parameters.AddWithValue("l", limit);
        var list = new List<ChatMessageRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadChat(r));
        }
        return list;
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveChatMessageAsync(System.Guid messageId, System.Guid senderId, System.DateTimeOffset at, System.DateTimeOffset notBefore, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("UPDATE chat_messages SET removed_at = @at, body = '' WHERE id = @id AND sender_id = @s AND removed_at IS NULL AND created_at >= @nb", conn);
        cmd.Parameters.AddWithValue("id", messageId);
        cmd.Parameters.AddWithValue("s", senderId);
        cmd.Parameters.AddWithValue("at", at.ToUniversalTime());
        cmd.Parameters.AddWithValue("nb", notBefore.ToUniversalTime());
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task MarkChatReadAsync(System.Guid roomId, System.Guid mailboxId, System.DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("UPDATE chat_members SET last_read_at = GREATEST(last_read_at, @at) WHERE room_id = @r AND mailbox_id = @m", conn);
        cmd.Parameters.AddWithValue("r", roomId);
        cmd.Parameters.AddWithValue("m", mailboxId);
        cmd.Parameters.AddWithValue("at", at.ToUniversalTime());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> CountUnreadChatsAsync(System.Guid mailboxId, CancellationToken ct = default)
    {
        const string sql = @"SELECT count(*) FROM chat_members m WHERE m.mailbox_id = @m AND m.left_at IS NULL
    AND EXISTS (SELECT 1 FROM chat_messages x WHERE x.room_id = m.room_id AND x.sender_id <> @m AND x.created_at > m.last_read_at AND x.removed_at IS NULL)";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("m", mailboxId);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
    }

    /// <inheritdoc/>
    public async Task TouchPresenceAsync(System.Guid mailboxId, System.DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("INSERT INTO presence (mailbox_id, last_seen_at) VALUES (@m, @at) ON CONFLICT (mailbox_id) DO UPDATE SET last_seen_at = GREATEST(presence.last_seen_at, EXCLUDED.last_seen_at)", conn);
        cmd.Parameters.AddWithValue("m", mailboxId);
        cmd.Parameters.AddWithValue("at", at.ToUniversalTime());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SetPresenceAsync(System.Guid mailboxId, string status, string note, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(status);
        System.ArgumentNullException.ThrowIfNull(note);
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("INSERT INTO presence (mailbox_id, status, note) VALUES (@m, @s, @n) ON CONFLICT (mailbox_id) DO UPDATE SET status = EXCLUDED.status, note = EXCLUDED.note, updated_at = now()", conn);
        cmd.Parameters.AddWithValue("m", mailboxId);
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.AddWithValue("n", note);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<System.Guid, PresenceRow>> GetPresenceAsync(IReadOnlyCollection<System.Guid> mailboxIds, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(mailboxIds);
        var found = new Dictionary<System.Guid, PresenceRow>();
        if (mailboxIds.Count == 0)
        {
            return found;
        }
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT mailbox_id, last_seen_at, status, note FROM presence WHERE mailbox_id = ANY(@ids)", conn);
        cmd.Parameters.AddWithValue("ids", mailboxIds.Distinct().ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            found[r.GetGuid(0)] = new PresenceRow
            {
                MailboxId = r.GetGuid(0),
                LastSeenAt = r.IsDBNull(1) ? null : r.GetFieldValue<System.DateTimeOffset>(1),
                Status = r.GetString(2),
                Note = r.GetString(3),
            };
        }
        return found;
    }

    private static ChatRoomRow ReadRoom(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        TenantId = r.GetGuid(1),
        Name = r.GetString(2),
        IsGroup = r.GetBoolean(3),
        CreatedBy = r.GetGuid(4),
        CreatedAt = r.GetFieldValue<System.DateTimeOffset>(5),
        LastMessageAt = r.GetFieldValue<System.DateTimeOffset>(6),
    };

    private static ChatMessageRow ReadChat(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        RoomId = r.GetGuid(1),
        SenderId = r.GetGuid(2),
        Body = r.GetString(3),
        CreatedAt = r.GetFieldValue<System.DateTimeOffset>(4),
        RemovedAt = r.IsDBNull(5) ? null : r.GetFieldValue<System.DateTimeOffset>(5),
    };
}
