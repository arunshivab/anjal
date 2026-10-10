using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>One message rescued from Junk with "Not spam" (rc.15, item 58).</summary>
/// <param name="Id">The message.</param>
/// <param name="At">When.</param>
public sealed record RescuedMessage(Guid Id, DateTimeOffset At);

/// <summary>How a person works with mail (rc.15, item 58, "Your habits"): seen only by them.</summary>
/// <param name="MedianReply">How long they usually take to reply, or null when they have not replied in the period.</param>
/// <param name="Replies">How many replies it is worked out from.</param>
/// <param name="ByHour">Messages received and sent in each hour of the day, 0 to 23, in their zone.</param>
/// <param name="Busiest">The busiest hours, busiest first (up to three).</param>
public sealed record MailHabits(TimeSpan? MedianReply, int Replies, IReadOnlyList<long> ByHour, IReadOnlyList<int> Busiest);

/// <summary>
/// rc.15 (item 58): the person's dashboard - mail rescued from Junk, the
/// space each folder takes, and "Your habits" (how quickly they reply and
/// their busiest hours), worked out only for them and shown only to them.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The kind of the document listing messages rescued from Junk.</summary>
    public const string RescuedKind = "junk-rescued";

    /// <summary>Messages rescued from Junk since a moment, newest first.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="since">From when.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The messages.</returns>
    public async Task<IReadOnlyList<RescuedMessage>> RescuedSinceAsync(Guid mailboxId, DateTimeOffset since, CancellationToken ct = default)
    {
        List<RescuedMessage> all = await this.ReadDocumentAsync<List<RescuedMessage>>(mailboxId, RescuedKind, ct).ConfigureAwait(false) ?? new List<RescuedMessage>();
        return all.Where(r => r.At >= since).OrderByDescending(r => r.At).ToList();
    }

    /// <summary>The space each folder takes, biggest first, with the folder's message count.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Each folder's name, bytes and messages; empty folders left out.</returns>
    public async Task<IReadOnlyList<(string Folder, long Bytes, long Count)>> FolderSpaceAsync(Guid mailboxId, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, long> bytes = await this.store.SumFolderBytesAsync(mailboxId, ct).ConfigureAwait(false);
        IReadOnlyList<FolderView> folders = await this.ListFoldersAsync(mailboxId, ct).ConfigureAwait(false);
        return folders
            .Select(f => (f.Name, bytes.TryGetValue(f.Id, out long b) ? b : 0L, f.Count))
            .Where(f => f.Item2 > 0)
            .OrderByDescending(f => f.Item2)
            .ToList();
    }

    /// <summary>
    /// "Your habits" over the last 30 days: how long the person usually takes
    /// to reply (from a message arriving to their first answer in the same
    /// conversation, within 14 days), and their busiest hours.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="clock">The person's zone.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The habits.</returns>
    public async Task<MailHabits> HabitsAsync(Guid mailboxId, ZonedClock clock, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        DateTimeOffset since = now.AddDays(-30);
        var gaps = new List<TimeSpan>();
        foreach (IReadOnlyList<ConversationItem> items in (await this.ConversationIndexAsync(mailboxId, ct).ConfigureAwait(false)).Values)
        {
            List<ConversationItem> ordered = items.OrderBy(i => i.At).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                ConversationItem got = ordered[i];
                if (got.Folder is "Sent" or "Drafts" or Anjal.Mailbox.MailboxSink.JunkFolder || got.At < since.AddDays(-14))
                {
                    continue;
                }
                // The first answer after it - unless another message came in first, which is then the one answered.
                for (int j = i + 1; j < ordered.Count; j++)
                {
                    if (ordered[j].Folder == "Sent")
                    {
                        TimeSpan gap = ordered[j].At - got.At;
                        if (ordered[j].At >= since && gap <= TimeSpan.FromDays(14))
                        {
                            gaps.Add(gap);
                        }
                        break;
                    }
                    if (ordered[j].Folder is not ("Drafts" or Anjal.Mailbox.MailboxSink.JunkFolder))
                    {
                        break;
                    }
                }
            }
        }
        gaps.Sort();
        TimeSpan? median = gaps.Count == 0 ? null : gaps.Count % 2 == 1 ? gaps[gaps.Count / 2] : (gaps[(gaps.Count / 2) - 1] + gaps[gaps.Count / 2]) / 2;

        var period = new DashPeriod("30d", "Last 30 days", since, now, TimeStep.Hour);
        MailFigures figures = await this.FiguresAsync(FigureScope.Mailbox(mailboxId), period, clock, ct).ConfigureAwait(false);
        long[] byHour = new long[24];
        foreach (TimeBucket b in figures.Series)
        {
            byHour[b.Start.Hour] += b.Received + b.Sent;
        }
        IReadOnlyList<int> busiest = Enumerable.Range(0, 24).Where(h => byHour[h] > 0).OrderByDescending(h => byHour[h]).ThenBy(h => h).Take(3).ToList();
        return new MailHabits(median, gaps.Count, byHour, busiest);
    }

    // Kept for a year, at most 1,000: enough for the dashboard's longest period.
    private async Task NoteRescuedAsync(Guid mailboxId, Guid messageId, CancellationToken ct)
    {
        List<RescuedMessage> all = await this.ReadDocumentAsync<List<RescuedMessage>>(mailboxId, RescuedKind, ct).ConfigureAwait(false) ?? new List<RescuedMessage>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        all.RemoveAll(r => r.Id == messageId || r.At < now.AddDays(-366));
        all.Add(new RescuedMessage(messageId, now));
        await this.WriteDocumentAsync(mailboxId, RescuedKind, all.TakeLast(1000).ToList(), ct).ConfigureAwait(false);
    }
}
