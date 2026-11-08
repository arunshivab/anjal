using Npgsql;

namespace Anjal.Store;

/// <summary>rc.27 additions to the PostgreSQL store: shared desks and the draft clean-up - see <c>tools/sql/schema.sql</c>.</summary>
public sealed partial class PostgresMessageStore
{
    private const string DeskItemColumns = "id, desk_id, thread_key, root_message_id, assignee_id, done_at, created_at, updated_at, pending_since, last_reply_at";
    private const string DeskNoteColumns = "id, item_id, at, writer_id, body, mentions, withdrawn_at";

    /// <inheritdoc/>
    public async Task<DeskItemRow> GetOrAddDeskItemAsync(System.Guid deskId, string threadKey, System.Guid? rootMessageId, System.DateTimeOffset? pendingSince, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(threadKey);
        // Two members acting at once: the second insert does nothing and the one row is read back.
        const string sql = @"
INSERT INTO desk_items (desk_id, thread_key, root_message_id, pending_since) VALUES (@d, @k, @r, @p)
ON CONFLICT (desk_id, thread_key) DO NOTHING;
SELECT " + DeskItemColumns + " FROM desk_items WHERE desk_id = @d AND thread_key = @k;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("d", deskId);
        cmd.Parameters.AddWithValue("k", threadKey);
        cmd.Parameters.Add(new NpgsqlParameter<System.Guid?>("r", NpgsqlTypes.NpgsqlDbType.Uuid) { TypedValue = rootMessageId });
        cmd.Parameters.Add(new NpgsqlParameter<System.DateTimeOffset?>("p", NpgsqlTypes.NpgsqlDbType.TimestampTz) { TypedValue = pendingSince?.ToUniversalTime() });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new System.InvalidOperationException("The desk item was not kept.");
        }
        return ReadDeskItem(reader);
    }

    /// <inheritdoc/>
    public async Task<(DeskItemRow Item, bool Reopened)> NoteDeskArrivalAsync(System.Guid deskId, string threadKey, System.Guid messageId, System.DateTimeOffset at, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(threadKey);
        // One statement: the row's state before it (the CTE sees the table as it was) says whether it reopened.
        const string sql = @"
WITH before AS (SELECT done_at FROM desk_items WHERE desk_id = @d AND thread_key = @k)
INSERT INTO desk_items (desk_id, thread_key, root_message_id, pending_since) VALUES (@d, @k, @r, @at)
ON CONFLICT (desk_id, thread_key) DO UPDATE SET
    pending_since = CASE WHEN desk_items.done_at IS NOT NULL OR desk_items.pending_since IS NULL THEN EXCLUDED.pending_since ELSE desk_items.pending_since END,
    done_at = NULL,
    updated_at = now()
RETURNING " + DeskItemColumns + ", (SELECT done_at FROM before) IS NOT NULL;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("d", deskId);
        cmd.Parameters.AddWithValue("k", threadKey);
        cmd.Parameters.AddWithValue("r", messageId);
        cmd.Parameters.AddWithValue("at", at.ToUniversalTime());
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new System.InvalidOperationException("The desk item was not kept.");
        }
        return (ReadDeskItem(reader), reader.GetBoolean(10));
    }

    /// <inheritdoc/>
    public async Task<(DeskItemRow Item, System.DateTimeOffset? WasPending)> NoteDeskReplyAsync(System.Guid deskId, string threadKey, System.Guid? messageId, System.DateTimeOffset at, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(threadKey);
        const string sql = @"
WITH before AS (SELECT pending_since FROM desk_items WHERE desk_id = @d AND thread_key = @k)
INSERT INTO desk_items (desk_id, thread_key, root_message_id, last_reply_at) VALUES (@d, @k, @r, @at)
ON CONFLICT (desk_id, thread_key) DO UPDATE SET pending_since = NULL, last_reply_at = EXCLUDED.last_reply_at, updated_at = now()
RETURNING " + DeskItemColumns + ", (SELECT pending_since FROM before);";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("d", deskId);
        cmd.Parameters.AddWithValue("k", threadKey);
        cmd.Parameters.Add(new NpgsqlParameter<System.Guid?>("r", NpgsqlTypes.NpgsqlDbType.Uuid) { TypedValue = messageId });
        cmd.Parameters.AddWithValue("at", at.ToUniversalTime());
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new System.InvalidOperationException("The desk item was not kept.");
        }
        return (ReadDeskItem(reader), reader.IsDBNull(10) ? null : reader.GetFieldValue<System.DateTimeOffset>(10));
    }

    /// <inheritdoc/>
    public async Task<DeskItemRow?> GetDeskItemAsync(System.Guid itemId, CancellationToken ct = default)
    {
        const string sql = "SELECT " + DeskItemColumns + " FROM desk_items WHERE id = @id;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", itemId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadDeskItem(reader) : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DeskItemRow>> ListDeskItemsAsync(System.Guid deskId, CancellationToken ct = default)
    {
        const string sql = "SELECT " + DeskItemColumns + " FROM desk_items WHERE desk_id = @d;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("d", deskId);
        return await ReadDeskItemsAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DeskItemRow>> ListAssignedDeskItemsAsync(System.Guid assigneeId, CancellationToken ct = default)
    {
        const string sql = "SELECT " + DeskItemColumns + " FROM desk_items WHERE assignee_id = @a AND done_at IS NULL;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("a", assigneeId);
        return await ReadDeskItemsAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> SetDeskAssigneeAsync(System.Guid itemId, System.Guid? assigneeId, CancellationToken ct = default)
    {
        const string sql = "UPDATE desk_items SET assignee_id = @a, updated_at = now() WHERE id = @id;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", itemId);
        cmd.Parameters.Add(new NpgsqlParameter<System.Guid?>("a", NpgsqlTypes.NpgsqlDbType.Uuid) { TypedValue = assigneeId });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> SetDeskDoneAsync(System.Guid itemId, System.DateTimeOffset? doneAt, CancellationToken ct = default)
    {
        const string sql = "UPDATE desk_items SET done_at = @done, updated_at = now() WHERE id = @id;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", itemId);
        cmd.Parameters.Add(new NpgsqlParameter<System.DateTimeOffset?>("done", NpgsqlTypes.NpgsqlDbType.TimestampTz) { TypedValue = doneAt?.ToUniversalTime() });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task AddDeskEventAsync(DeskEventRow row, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(row);
        const string sql = "INSERT INTO desk_events (item_id, at, actor_id, kind, detail) SELECT @i, @at, @a, @k, @d WHERE EXISTS (SELECT 1 FROM desk_items WHERE id = @i);";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("i", row.ItemId);
        cmd.Parameters.AddWithValue("at", (row.At == default ? System.DateTimeOffset.UtcNow : row.At).ToUniversalTime());
        cmd.Parameters.Add(new NpgsqlParameter<System.Guid?>("a", NpgsqlTypes.NpgsqlDbType.Uuid) { TypedValue = row.ActorId });
        cmd.Parameters.AddWithValue("k", row.Kind);
        cmd.Parameters.AddWithValue("d", row.Detail);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DeskEventRow>> ListDeskEventsAsync(System.Guid itemId, CancellationToken ct = default)
    {
        const string sql = "SELECT id, item_id, at, actor_id, kind, detail FROM desk_events WHERE item_id = @i ORDER BY at, id;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("i", itemId);
        return await ReadDeskEventsAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DeskEventRow>> ListDeskEventsSinceAsync(System.Guid deskId, System.DateTimeOffset since, CancellationToken ct = default)
    {
        const string sql = @"SELECT e.id, e.item_id, e.at, e.actor_id, e.kind, e.detail FROM desk_events e JOIN desk_items i ON i.id = e.item_id
WHERE i.desk_id = @d AND e.at >= @since ORDER BY e.at, e.id;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("d", deskId);
        cmd.Parameters.AddWithValue("since", since.ToUniversalTime());
        return await ReadDeskEventsAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task AddDeskNoteAsync(DeskNoteRow row, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(row);
        if (row.Id == System.Guid.Empty)
        {
            row.Id = System.Guid.NewGuid();
        }
        if (row.At == default)
        {
            row.At = System.DateTimeOffset.UtcNow;
        }
        const string sql = "INSERT INTO desk_notes (id, item_id, at, writer_id, body, mentions) SELECT @id, @i, @at, @w, @b, @m WHERE EXISTS (SELECT 1 FROM desk_items WHERE id = @i);";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", row.Id);
        cmd.Parameters.AddWithValue("i", row.ItemId);
        cmd.Parameters.AddWithValue("at", row.At.ToUniversalTime());
        cmd.Parameters.Add(new NpgsqlParameter<System.Guid?>("w", NpgsqlTypes.NpgsqlDbType.Uuid) { TypedValue = row.WriterId });
        cmd.Parameters.AddWithValue("b", row.Body);
        cmd.Parameters.AddWithValue("m", row.Mentions);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DeskNoteRow>> ListDeskNotesAsync(System.Guid itemId, CancellationToken ct = default)
    {
        const string sql = "SELECT " + DeskNoteColumns + " FROM desk_notes WHERE item_id = @i ORDER BY at;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("i", itemId);
        var list = new List<DeskNoteRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new DeskNoteRow
            {
                Id = reader.GetGuid(0),
                ItemId = reader.GetGuid(1),
                At = reader.GetFieldValue<System.DateTimeOffset>(2),
                WriterId = reader.IsDBNull(3) ? null : reader.GetGuid(3),
                Body = reader.GetString(4),
                Mentions = reader.GetString(5),
                WithdrawnAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<System.DateTimeOffset>(6),
            });
        }
        return list;
    }

    /// <inheritdoc/>
    public async Task<bool> WithdrawDeskNoteAsync(System.Guid noteId, System.Guid itemId, System.Guid writerId, System.DateTimeOffset at, CancellationToken ct = default)
    {
        const string sql = "UPDATE desk_notes SET withdrawn_at = @at WHERE id = @id AND item_id = @i AND writer_id = @w AND withdrawn_at IS NULL;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", noteId);
        cmd.Parameters.AddWithValue("i", itemId);
        cmd.Parameters.AddWithValue("w", writerId);
        cmd.Parameters.AddWithValue("at", at.ToUniversalTime());
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MessageRow>> ListEmptyDraftsAsync(System.DateTimeOffset before, int limit, CancellationToken ct = default)
    {
        string sql = "SELECT " + MessageColumns + @" FROM messages
WHERE draft_empty AND received_at < @before AND folder_id IN (SELECT id FROM folders WHERE name = 'Drafts')
ORDER BY received_at LIMIT @limit;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("before", before.ToUniversalTime());
        cmd.Parameters.AddWithValue("limit", limit);
        var list = new List<MessageRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadMailboxMessage(reader));
        }
        return list;
    }

    private static DeskItemRow ReadDeskItem(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        DeskId = r.GetGuid(1),
        ThreadKey = r.GetString(2),
        RootMessageId = r.IsDBNull(3) ? null : r.GetGuid(3),
        AssigneeId = r.IsDBNull(4) ? null : r.GetGuid(4),
        DoneAt = r.IsDBNull(5) ? null : r.GetFieldValue<System.DateTimeOffset>(5),
        CreatedAt = r.GetFieldValue<System.DateTimeOffset>(6),
        UpdatedAt = r.GetFieldValue<System.DateTimeOffset>(7),
        PendingSince = r.IsDBNull(8) ? null : r.GetFieldValue<System.DateTimeOffset>(8),
        LastReplyAt = r.IsDBNull(9) ? null : r.GetFieldValue<System.DateTimeOffset>(9),
    };

    private static async Task<IReadOnlyList<DeskItemRow>> ReadDeskItemsAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<DeskItemRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadDeskItem(reader));
        }
        return list;
    }

    private static async Task<IReadOnlyList<DeskEventRow>> ReadDeskEventsAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<DeskEventRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new DeskEventRow
            {
                Id = reader.GetInt64(0),
                ItemId = reader.GetGuid(1),
                At = reader.GetFieldValue<System.DateTimeOffset>(2),
                ActorId = reader.IsDBNull(3) ? null : reader.GetGuid(3),
                Kind = reader.GetString(4),
                Detail = reader.GetString(5),
            });
        }
        return list;
    }
}
