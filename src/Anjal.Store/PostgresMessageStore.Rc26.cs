using Npgsql;

namespace Anjal.Store;

/// <summary>rc.26 additions to the PostgreSQL store - see <c>tools/sql/schema.sql</c>.</summary>
public sealed partial class PostgresMessageStore
{
    /// <inheritdoc/>
    public async Task<long> CountListMailAsync(System.Guid mailboxId, string listMark, System.DateTimeOffset since, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(listMark);
        if (listMark.Length == 0)
        {
            return 0;
        }
        const string sql = "SELECT count(*) FROM messages WHERE mailbox_id = @m AND list_mark = @l AND received_at >= @since;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("m", mailboxId);
        cmd.Parameters.AddWithValue("l", listMark);
        cmd.Parameters.AddWithValue("since", since.ToUniversalTime());
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long n ? n : 0;
    }

    /// <inheritdoc/>
    public async Task TouchSignInAsync(System.Guid mailboxId, System.DateTimeOffset at, CancellationToken ct = default)
    {
        const string sql = "UPDATE mailboxes SET last_sign_in_at = @at WHERE id = @id AND last_sign_in_at < @at;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", mailboxId);
        cmd.Parameters.AddWithValue("at", at.ToUniversalTime());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
