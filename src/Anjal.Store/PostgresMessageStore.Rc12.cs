using Npgsql;

namespace Anjal.Store;

/// <summary>
/// rc.12 additions to the PostgreSQL store: a mailbox's own documents
/// (<c>mailbox_documents</c>) and the Outbox - see <c>tools/sql/schema.sql</c>.
/// </summary>
public sealed partial class PostgresMessageStore
{
    /// <inheritdoc/>
    public async Task<bool> DeleteEmptyFolderAsync(System.Guid mailboxId, System.Guid folderId, CancellationToken ct = default)
    {
        const string sql = @"
DELETE FROM folders f
WHERE f.id = @id AND f.mailbox_id = @mailbox
  AND NOT EXISTS (SELECT 1 FROM messages m WHERE m.folder_id = f.id);";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", folderId);
        cmd.Parameters.AddWithValue("mailbox", mailboxId);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task<string?> GetMailboxDocumentAsync(System.Guid mailboxId, string kind, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(kind);
        const string sql = "SELECT body FROM mailbox_documents WHERE mailbox_id = @mailbox AND kind = @kind;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("mailbox", mailboxId);
        cmd.Parameters.AddWithValue("kind", kind);
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as string;
    }

    /// <inheritdoc/>
    public async Task SetMailboxDocumentAsync(System.Guid mailboxId, string kind, string json, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(kind);
        System.ArgumentNullException.ThrowIfNull(json);
        const string sql = @"
INSERT INTO mailbox_documents (mailbox_id, kind, body, updated_at)
VALUES (@mailbox, @kind, @body, now())
ON CONFLICT (mailbox_id, kind) DO UPDATE SET body = EXCLUDED.body, updated_at = now();";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("mailbox", mailboxId);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("body", json);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OutboundMessage>> ListOutboundForSenderAsync(string envelopeFrom, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(envelopeFrom);
        const string sql = @"
SELECT id, envelope_from, envelope_to, raw_bytes, status, attempts, created_at, next_attempt_at, give_up_at, last_error
FROM outbound_messages
WHERE lower(envelope_from) = lower(@from) AND status IN (0, 1)
ORDER BY created_at
LIMIT 500;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("from", envelopeFrom);
        var result = new List<OutboundMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadOutbound(reader));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<bool> CancelOutboundAsync(System.Guid id, string envelopeFrom, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(envelopeFrom);
        const string sql = @"
UPDATE outbound_messages SET status = 3, last_error = @why
WHERE id = @id AND status = 0 AND lower(envelope_from) = lower(@from);";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("from", envelopeFrom);
        cmd.Parameters.AddWithValue("why", OutboundMessage.CancelledBySender);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task<bool> RetryOutboundNowAsync(System.Guid id, string envelopeFrom, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(envelopeFrom);
        const string sql = @"
UPDATE outbound_messages SET next_attempt_at = now()
WHERE id = @id AND status = 0 AND lower(envelope_from) = lower(@from);";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("from", envelopeFrom);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }
}
