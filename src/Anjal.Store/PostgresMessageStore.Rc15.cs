using Npgsql;

namespace Anjal.Store;

/// <summary>
/// rc.15 additions to the PostgreSQL store: what the dashboards count, as
/// totals worked out by the database (a busy organisation is millions of
/// rows; nothing is read into memory to be counted).
/// </summary>
public sealed partial class PostgresMessageStore
{
    // Messages in the scope and period, with their folder's name. A scope with
    // neither a mailbox nor an organisation is the whole service.
    private const string ScopedMessages = @"
FROM messages m
JOIN folders f ON f.id = m.folder_id
JOIN mailboxes b ON b.id = m.mailbox_id
WHERE m.received_at >= @from AND m.received_at < @to
  AND (@mailbox IS NULL OR m.mailbox_id = @mailbox)
  AND (@tenant IS NULL OR b.tenant_id = @tenant)
  AND f.name <> 'Drafts'";

    /// <inheritdoc/>
    public async Task<MailFigures> GetMailFiguresAsync(FigureScope scope, System.DateTimeOffset periodStart, System.DateTimeOffset periodEnd, string timeZone, TimeStep stepBy, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(scope);
        // Only a zone this server knows reaches the query (DEF-088).
        string zone = MailboxPreferences.IsKnownTimeZone(timeZone) ? timeZone.Trim() : "UTC";
        const string totalsSql = @"
SELECT count(*) FILTER (WHERE f.name NOT IN ('Sent', 'Junk')),
       count(*) FILTER (WHERE f.name = 'Sent'),
       count(*) FILTER (WHERE f.name = 'Junk'),
       count(*) FILTER (WHERE f.name <> 'Sent' AND m.spam_checked IS TRUE)" + ScopedMessages + ";";
        const string seriesSql = @"
SELECT date_trunc(@step, m.received_at AT TIME ZONE @tz) AS bucket,
       count(*) FILTER (WHERE f.name NOT IN ('Sent', 'Junk')),
       count(*) FILTER (WHERE f.name = 'Sent')" + ScopedMessages + @"
GROUP BY bucket ORDER BY bucket;";
        const string scoresSql = @"
SELECT least(greatest(m.spam_score, 0), 10) AS s, count(*)" + ScopedMessages + @"
  AND f.name <> 'Sent' AND m.spam_checked IS TRUE
GROUP BY s;";

        var figures = new MailFigures();
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using (var cmd = new NpgsqlCommand(totalsSql, conn))
        {
            BindScope(cmd, scope, periodStart, periodEnd);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                figures.Received = r.GetInt64(0);
                figures.Sent = r.GetInt64(1);
                figures.Junk = r.GetInt64(2);
                figures.Checked = r.GetInt64(3);
            }
        }
        await using (var cmd = new NpgsqlCommand(seriesSql, conn))
        {
            BindScope(cmd, scope, periodStart, periodEnd);
            cmd.Parameters.AddWithValue("tz", zone);
            cmd.Parameters.AddWithValue("step", stepBy switch { TimeStep.Hour => "hour", TimeStep.Day => "day", TimeStep.Week => "week", _ => "month" });
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var series = new List<TimeBucket>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                series.Add(new TimeBucket(System.DateTime.SpecifyKind(r.GetDateTime(0), System.DateTimeKind.Unspecified), r.GetInt64(1), r.GetInt64(2)));
            }
            figures.Series = series;
        }
        await using (var cmd = new NpgsqlCommand(scoresSql, conn))
        {
            BindScope(cmd, scope, periodStart, periodEnd);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                int score = r.GetInt32(0);
                if (score is >= 0 and <= MailFigures.TopScore)
                {
                    figures.Scores[score] = r.GetInt64(1);
                }
            }
        }
        return figures;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<System.Guid, long>> CountReceivedByTenantAsync(System.DateTimeOffset periodStart, System.DateTimeOffset periodEnd, CancellationToken ct = default)
    {
        const string sql = @"
SELECT b.tenant_id, count(*)
FROM messages m
JOIN folders f ON f.id = m.folder_id
JOIN mailboxes b ON b.id = m.mailbox_id
WHERE m.received_at >= @from AND m.received_at < @to AND f.name NOT IN ('Sent', 'Drafts')
GROUP BY b.tenant_id;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("from", periodStart);
        cmd.Parameters.AddWithValue("to", periodEnd);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new Dictionary<System.Guid, long>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result[r.GetGuid(0)] = r.GetInt64(1);
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SenderTraffic>> CountOutboundBySenderAsync(IReadOnlyCollection<string> senders, System.DateTimeOffset periodStart, System.DateTimeOffset periodEnd, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(senders);
        if (senders.Count == 0)
        {
            return System.Array.Empty<SenderTraffic>();
        }
        const string sql = @"
SELECT lower(envelope_from),
       count(*) FILTER (WHERE status = 2),
       count(*) FILTER (WHERE status IN (0, 1)),
       count(*) FILTER (WHERE status = 3)
FROM outbound_messages
WHERE created_at >= @from AND created_at < @to AND lower(envelope_from) = ANY(@senders)
GROUP BY lower(envelope_from);";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("from", periodStart);
        cmd.Parameters.AddWithValue("to", periodEnd);
        cmd.Parameters.AddWithValue("senders", senders.Select(s => s.ToLowerInvariant()).ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var found = new Dictionary<string, SenderTraffic>(System.StringComparer.OrdinalIgnoreCase);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            found[r.GetString(0)] = new SenderTraffic(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
        }
        return senders.Select(s => found.TryGetValue(s, out SenderTraffic? t) ? t with { Sender = s } : new SenderTraffic(s, 0, 0, 0)).ToList();
    }

    private static void BindScope(NpgsqlCommand cmd, FigureScope scope, System.DateTimeOffset periodStart, System.DateTimeOffset periodEnd)
    {
        cmd.Parameters.AddWithValue("from", periodStart);
        cmd.Parameters.AddWithValue("to", periodEnd);
        cmd.Parameters.Add(new NpgsqlParameter<System.Guid?>("mailbox", NpgsqlTypes.NpgsqlDbType.Uuid) { TypedValue = scope.MailboxId });
        cmd.Parameters.Add(new NpgsqlParameter<System.Guid?>("tenant", NpgsqlTypes.NpgsqlDbType.Uuid) { TypedValue = scope.TenantId });
    }
}
