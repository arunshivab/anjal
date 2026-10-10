using Npgsql;

namespace Anjal.Store;

/// <summary>Folders put in the order a person chooses (owner, 9 Oct 2026), in the PostgreSQL store.</summary>
public sealed partial class PostgresMessageStore
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<MessageRow>> ListMessagesSortedAsync(System.Guid mailboxId, System.Guid folderId, bool? seen, string sort, int limit, int offset, CancellationToken ct = default)
    {
        // Only fixed text goes into the ORDER BY: the choice picks one of these, never the person's words.
        string order = sort switch
        {
            "oldest" => "received_at ASC, id ASC",
            "subject" => "lower(regexp_replace(subject, '^\\s*((re|fwd?|aw)\\s*:\\s*)+', '', 'i')) ASC, received_at DESC, id DESC",
            "size" => "size_bytes DESC, received_at DESC, id DESC",
            "unread" => "seen ASC, received_at DESC, id DESC",
            "attachments" => "has_attachments DESC, received_at DESC, id DESC",
            _ => "received_at DESC, id DESC",
        };
        string sql = "SELECT " + MessageColumns + @"
FROM messages
WHERE mailbox_id = @mailbox_id AND folder_id = @folder_id AND (@seen IS NULL OR seen = @seen)
ORDER BY " + order + @"
LIMIT @limit OFFSET @offset;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("mailbox_id", mailboxId);
        cmd.Parameters.AddWithValue("folder_id", folderId);
        cmd.Parameters.Add(new NpgsqlParameter<bool?>("seen", NpgsqlTypes.NpgsqlDbType.Boolean) { TypedValue = seen });
        cmd.Parameters.AddWithValue("limit", System.Math.Max(0, limit));
        cmd.Parameters.AddWithValue("offset", System.Math.Max(0, offset));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<MessageRow>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadMailboxMessage(reader));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MessageNames>> ListMessageNamesAsync(System.Guid mailboxId, System.Guid folderId, bool? seen, CancellationToken ct = default)
    {
        const string sql = @"
SELECT id, from_header, to_header, envelope_from, received_at
FROM messages
WHERE mailbox_id = @mailbox_id AND folder_id = @folder_id AND (@seen IS NULL OR seen = @seen);";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("mailbox_id", mailboxId);
        cmd.Parameters.AddWithValue("folder_id", folderId);
        cmd.Parameters.Add(new NpgsqlParameter<bool?>("seen", NpgsqlTypes.NpgsqlDbType.Boolean) { TypedValue = seen });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<MessageNames>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new MessageNames(
                r.GetGuid(0),
                r.IsDBNull(1) ? string.Empty : r.GetString(1),
                r.IsDBNull(2) ? string.Empty : r.GetString(2),
                r.IsDBNull(3) ? string.Empty : r.GetString(3),
                r.GetFieldValue<System.DateTimeOffset>(4)));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MessageRow>> ListMessagesByIdsAsync(System.Guid mailboxId, IReadOnlyList<System.Guid> ids, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return System.Array.Empty<MessageRow>();
        }
        const string sql = "SELECT " + MessageColumns + " FROM messages WHERE mailbox_id = @mailbox_id AND id = ANY(@ids);";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("mailbox_id", mailboxId);
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<MessageRow>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadMailboxMessage(reader));
        }
        return result;
    }
}
