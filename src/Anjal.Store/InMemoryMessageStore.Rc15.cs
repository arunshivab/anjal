namespace Anjal.Store;

/// <summary>rc.15 additions to the in-memory store: what the dashboards count.</summary>
public sealed partial class InMemoryMessageStore
{
    /// <inheritdoc/>
    public Task<MailFigures> GetMailFiguresAsync(FigureScope scope, System.DateTimeOffset periodStart, System.DateTimeOffset periodEnd, string timeZone, TimeStep stepBy, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(scope);
        System.TimeZoneInfo zone = System.TimeZoneInfo.FindSystemTimeZoneById(MailboxPreferences.IsKnownTimeZone(timeZone) ? timeZone.Trim() : "UTC");
        lock (this.gate)
        {
            HashSet<System.Guid> boxes = this.mailboxes
                .Where(b => (scope.MailboxId is null || b.Id == scope.MailboxId) && (scope.TenantId is null || b.TenantId == scope.TenantId))
                .Select(b => b.Id)
                .ToHashSet();
            Dictionary<System.Guid, string> folderNames = this.folders.Where(f => boxes.Contains(f.MailboxId)).ToDictionary(f => f.Id, f => f.Name);
            var figures = new MailFigures();
            var series = new SortedDictionary<System.DateTime, (long Received, long Sent)>();
            foreach (MessageRow m in this.mailboxMessages)
            {
                if (!boxes.Contains(m.MailboxId) || m.ReceivedAt < periodStart || m.ReceivedAt >= periodEnd)
                {
                    continue;
                }
                string folder = folderNames.TryGetValue(m.FolderId, out string? f) ? f : string.Empty;
                if (folder == "Drafts")
                {
                    continue;
                }
                System.DateTime bucket = StepStart(System.TimeZoneInfo.ConvertTime(m.ReceivedAt, zone).DateTime, stepBy);
                series.TryGetValue(bucket, out (long Received, long Sent) b);
                if (folder == "Sent")
                {
                    figures.Sent++;
                    series[bucket] = (b.Received, b.Sent + 1);
                    continue;
                }
                if (folder == "Junk")
                {
                    figures.Junk++;
                }
                else
                {
                    figures.Received++;
                    series[bucket] = (b.Received + 1, b.Sent);
                }
                if (m.SpamChecked == true)
                {
                    figures.Checked++;
                    figures.Scores[System.Math.Clamp(m.SpamScore, 0, MailFigures.TopScore)]++;
                }
            }
            figures.Series = series.Select(kv => new TimeBucket(kv.Key, kv.Value.Received, kv.Value.Sent)).ToList();
            return Task.FromResult(figures);
        }
    }

    /// <summary>The start of the step a local time falls in: the hour, the day, the Monday, or the first of the month.</summary>
    /// <param name="local">The local time.</param>
    /// <param name="stepBy">The step.</param>
    /// <returns>The step's start.</returns>
    public static System.DateTime StepStart(System.DateTime local, TimeStep stepBy) => stepBy switch
    {
        TimeStep.Hour => new System.DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0, System.DateTimeKind.Unspecified),
        TimeStep.Day => System.DateTime.SpecifyKind(local.Date, System.DateTimeKind.Unspecified),
        TimeStep.Week => System.DateTime.SpecifyKind(local.Date.AddDays(-(((int)local.DayOfWeek + 6) % 7)), System.DateTimeKind.Unspecified),
        _ => new System.DateTime(local.Year, local.Month, 1, 0, 0, 0, System.DateTimeKind.Unspecified),
    };

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<System.Guid, long>> CountReceivedByTenantAsync(System.DateTimeOffset periodStart, System.DateTimeOffset periodEnd, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            Dictionary<System.Guid, System.Guid> tenantOf = this.mailboxes.ToDictionary(b => b.Id, b => b.TenantId);
            HashSet<System.Guid> notReceived = this.folders.Where(f => f.Name is "Sent" or "Drafts").Select(f => f.Id).ToHashSet();
            IReadOnlyDictionary<System.Guid, long> result = this.mailboxMessages
                .Where(m => m.ReceivedAt >= periodStart && m.ReceivedAt < periodEnd && !notReceived.Contains(m.FolderId) && tenantOf.ContainsKey(m.MailboxId))
                .GroupBy(m => tenantOf[m.MailboxId])
                .ToDictionary(g => g.Key, g => (long)g.Count());
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SenderTraffic>> CountOutboundBySenderAsync(IReadOnlyCollection<string> senders, System.DateTimeOffset periodStart, System.DateTimeOffset periodEnd, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(senders);
        lock (this.gate)
        {
            IReadOnlyList<SenderTraffic> result = senders
                .Select(s =>
                {
                    List<OutboundMessage> mine = this.outbound
                        .Where(o => string.Equals(o.EnvelopeFrom, s, System.StringComparison.OrdinalIgnoreCase) && o.CreatedAt >= periodStart && o.CreatedAt < periodEnd)
                        .ToList();
                    return new SenderTraffic(
                        s,
                        mine.Count(o => o.Status == OutboundStatus.Sent),
                        mine.Count(o => o.Status is OutboundStatus.Pending or OutboundStatus.Sending),
                        mine.Count(o => o.Status == OutboundStatus.Failed));
                })
                .ToList();
            return Task.FromResult(result);
        }
    }
}
