using System.Collections.Concurrent;
using Anjal.Mailbox;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>One snoozed message (rc.22, item 31): hidden in the Snoozed folder until its time.</summary>
public sealed class SnoozeEntry
{
    /// <summary>When it comes back.</summary>
    public DateTimeOffset Until { get; set; }

    /// <summary>The folder it came from, where it goes back to (INBOX unless it was elsewhere).</summary>
    public string Origin { get; set; } = FolderRow.Inbox;

    /// <summary>When it was snoozed.</summary>
    public DateTimeOffset Since { get; set; }
}

/// <summary>What a mailbox has snoozed, and what came back (rc.22, item 31), kept as its "snooze" document.</summary>
public sealed class SnoozeDocument
{
    /// <summary>The mailbox document kind.</summary>
    public const string Kind = "snooze";

    /// <summary>Snoozed messages, by message id.</summary>
    public Dictionary<Guid, SnoozeEntry> Sleeping { get; set; } = new();

    /// <summary>Messages that came back, by their new id, and when - marked "Snoozed" in the list for a day.</summary>
    public Dictionary<Guid, DateTimeOffset> Woken { get; set; } = new();
}

/// <summary>
/// rc.22 (item 31, mockup 15): snooze - hide a mail until a chosen time, then bring it back to the
/// top of the Inbox, marked "Snoozed". The mail waits in the Snoozed folder, which shows in the rail
/// while it holds anything; coming back, it is filed again as new mail, unread, so it is on top.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The folder snoozed mail waits in.</summary>
    public const string SnoozedFolder = "Snoozed";

    /// <summary>The presets offered: later today, tomorrow, this weekend, next week.</summary>
    public static readonly IReadOnlyList<string> SnoozePresets = new[] { "today", "tomorrow", "weekend", "week" };

    private static readonly string[] SnoozeTimeFormats = { "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss" };

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> snoozeNext = new();

    private DateTimeOffset snoozeScanned = DateTimeOffset.MinValue;

    /// <summary>The time a preset means, in the person's own clock: 18:00 today, 09:00 tomorrow, Saturday 09:00, next Monday 09:00.</summary>
    /// <param name="clock">The person's clock.</param>
    /// <param name="now">Now.</param>
    /// <param name="preset">One of <see cref="SnoozePresets"/>, or a wall-clock time "yyyy-MM-ddTHH:mm" picked by hand.</param>
    /// <returns>The instant, or null when the preset is not one.</returns>
    /// <param name="leaveUntil">rc.26 (item 73): when the person's automatic reply ends, for "leave"; null when it has no end.</param>
    public static DateTimeOffset? SnoozeTime(ZonedClock clock, DateTimeOffset now, string? preset, DateTimeOffset? leaveUntil = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        DateTimeOffset local = clock.Local(now);
        switch (preset)
        {
            case "leave":
                return leaveUntil is DateTimeOffset end && end > now ? AfterLeave(clock, end) : null;
            case "today":
                DateTime evening = local.Date.AddHours(18);
                return AtLocal(clock, local.Hour < 17 ? evening : local.DateTime.AddHours(3));
            case "tomorrow":
                return AtLocal(clock, local.Date.AddDays(1).AddHours(9));
            case "weekend":
                int toSaturday = ((int)DayOfWeek.Saturday - (int)local.DayOfWeek + 7) % 7;
                return AtLocal(clock, local.Date.AddDays(toSaturday == 0 ? 7 : toSaturday).AddHours(9));
            case "week":
                int toMonday = ((int)DayOfWeek.Monday - (int)local.DayOfWeek + 7) % 7;
                return AtLocal(clock, local.Date.AddDays(toMonday == 0 ? 7 : toMonday).AddHours(9));
            default:
                return DateTime.TryParseExact(preset ?? string.Empty, SnoozeTimeFormats,
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime picked)
                    ? AtLocal(clock, picked)
                    : null;
        }
    }

    /// <summary>
    /// rc.26 (item 73): "After my leave" - the first working morning after the automatic reply ends,
    /// at 09:00 in the person's own clock; Saturday and Sunday are skipped.
    /// </summary>
    /// <param name="clock">The person's clock.</param>
    /// <param name="until">When the automatic reply ends.</param>
    /// <returns>The instant.</returns>
    public static DateTimeOffset AfterLeave(ZonedClock clock, DateTimeOffset until)
    {
        ArgumentNullException.ThrowIfNull(clock);
        DateTimeOffset local = clock.Local(until);
        // The first 09:00 after the end: the same morning when it ends before nine, else the next.
        DateTime day = local.TimeOfDay < TimeSpan.FromHours(9) ? local.Date : local.Date.AddDays(1);
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            day = day.AddDays(1);
        }
        return AtLocal(clock, day.AddHours(9));
    }

    /// <summary>What a mailbox has snoozed.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The document.</returns>
    public async Task<SnoozeDocument> SnoozeOfAsync(Guid mailboxId, CancellationToken ct = default) =>
        await this.ReadDocumentAsync<SnoozeDocument>(mailboxId, SnoozeDocument.Kind, ct).ConfigureAwait(false) ?? new SnoozeDocument();

    /// <summary>Snooze a message until a time. Returns the error, or null.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The message.</param>
    /// <param name="until">When it comes back.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> SnoozeAsync(Guid mailboxId, Guid messageId, DateTimeOffset until, CancellationToken ct = default)
    {
        if (until <= DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return "Choose a time in the future.";
        }
        if (until > DateTimeOffset.UtcNow.AddDays(366))
        {
            return "Snooze for up to a year.";
        }
        MessageRow? row = await this.GetOwnedRowAsync(mailboxId, messageId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return "That message was not found.";
        }
        IReadOnlyList<FolderRow> folders = await this.store.ListFoldersAsync(mailboxId, ct).ConfigureAwait(false);
        string origin = folders.FirstOrDefault(f => f.Id == row.FolderId)?.Name ?? FolderRow.Inbox;
        if (origin is "Drafts" or "Sent" or ScheduledFolder or "Trash" or "Outbox")
        {
            return "Snooze works on mail you received.";
        }
        if (origin == SnoozedFolder)
        {
            SnoozeDocument existing = await this.SnoozeOfAsync(mailboxId, ct).ConfigureAwait(false);
            origin = existing.Sleeping.TryGetValue(messageId, out SnoozeEntry? e) ? e.Origin : FolderRow.Inbox;
        }
        else if (await this.store.EnsureFolderAsync(mailboxId, SnoozedFolder, ct).ConfigureAwait(false) is null
            || await this.MoveAsync(mailboxId, messageId, SnoozedFolder, ct).ConfigureAwait(false) is null)
        {
            return "That message could not be moved.";
        }
        SnoozeDocument doc = await this.SnoozeOfAsync(mailboxId, ct).ConfigureAwait(false);
        doc.Sleeping[messageId] = new SnoozeEntry { Until = until, Origin = origin, Since = DateTimeOffset.UtcNow };
        await this.WriteDocumentAsync(mailboxId, SnoozeDocument.Kind, doc, ct).ConfigureAwait(false);
        this.snoozeNext[mailboxId] = doc.Sleeping.Values.Min(s => s.Until);
        return null;
    }

    /// <summary>Bring a snoozed message back now.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The message.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> UnsnoozeAsync(Guid mailboxId, Guid messageId, CancellationToken ct = default)
    {
        SnoozeDocument doc = await this.SnoozeOfAsync(mailboxId, ct).ConfigureAwait(false);
        if (!doc.Sleeping.TryGetValue(messageId, out SnoozeEntry? entry))
        {
            return "That message is not snoozed.";
        }
        await this.WakeOneAsync(mailboxId, messageId, entry, doc, ct).ConfigureAwait(false);
        await this.WriteDocumentAsync(mailboxId, SnoozeDocument.Kind, doc, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Bring back the snoozed mail whose time has come, in every mailbox (every few seconds; every
    /// mailbox is looked at again every five minutes, in case another process snoozed something).
    /// </summary>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many came back.</returns>
    public async Task<int> WakeSnoozedAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        if (now - this.snoozeScanned > TimeSpan.FromMinutes(5))
        {
            this.snoozeScanned = now;
            foreach (MailboxRow m in await this.store.ListMailboxesAsync(null, ct).ConfigureAwait(false))
            {
                SnoozeDocument doc = await this.SnoozeOfAsync(m.Id, ct).ConfigureAwait(false);
                if (doc.Sleeping.Count > 0)
                {
                    this.snoozeNext[m.Id] = doc.Sleeping.Values.Min(s => s.Until);
                }
                else
                {
                    this.snoozeNext.TryRemove(m.Id, out _);
                }
            }
        }
        int woken = 0;
        foreach ((Guid mailboxId, DateTimeOffset next) in this.snoozeNext.ToArray())
        {
            if (next > now)
            {
                continue;
            }
            SnoozeDocument doc = await this.SnoozeOfAsync(mailboxId, ct).ConfigureAwait(false);
            foreach ((Guid messageId, SnoozeEntry entry) in doc.Sleeping.Where(kv => kv.Value.Until <= now).ToList())
            {
                await this.WakeOneAsync(mailboxId, messageId, entry, doc, ct).ConfigureAwait(false);
                woken++;
            }
            foreach (Guid old in doc.Woken.Where(kv => kv.Value < now.AddDays(-1)).Select(kv => kv.Key).ToList())
            {
                doc.Woken.Remove(old);
            }
            await this.WriteDocumentAsync(mailboxId, SnoozeDocument.Kind, doc, ct).ConfigureAwait(false);
            if (doc.Sleeping.Count > 0)
            {
                this.snoozeNext[mailboxId] = doc.Sleeping.Values.Min(s => s.Until);
            }
            else
            {
                this.snoozeNext.TryRemove(mailboxId, out _);
            }
        }
        return woken;
    }

    /// <summary>The messages that came back from snooze in the last day, by their id (for the "Snoozed" mark in the list).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The ids.</returns>
    public async Task<IReadOnlySet<Guid>> WokenAsync(Guid mailboxId, CancellationToken ct = default)
    {
        SnoozeDocument doc = await this.SnoozeOfAsync(mailboxId, ct).ConfigureAwait(false);
        DateTimeOffset since = DateTimeOffset.UtcNow.AddDays(-1);
        return doc.Woken.Where(kv => kv.Value >= since).Select(kv => kv.Key).ToHashSet();
    }

    // Back to its folder, unread so it is noticed, under the same id (links, audit lines and the
    // evidence record keep pointing at it); the folder puts woken mail on top for a day.
    private async Task WakeOneAsync(Guid mailboxId, Guid messageId, SnoozeEntry entry, SnoozeDocument doc, CancellationToken ct)
    {
        doc.Sleeping.Remove(messageId);
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        MessageRow? row = await this.GetOwnedRowAsync(mailboxId, messageId, ct).ConfigureAwait(false);
        if (context is null || row is null)
        {
            return;
        }
        string origin = MailboxSink.IsRuleTarget(entry.Origin) || entry.Origin == FolderRow.Inbox ? entry.Origin : FolderRow.Inbox;
        MessageRow? moved = await this.MoveAsync(mailboxId, messageId, origin, ct).ConfigureAwait(false);
        if (moved is null)
        {
            return;
        }
        if (moved.Seen)
        {
            FolderRow? folder = await this.FolderByIdAsync(mailboxId, moved.FolderId, ct).ConfigureAwait(false);
            // The file's flags go first, since its name carries them.
            string? file = folder is null ? null : this.maildir.SetFlags(context.Value.Tenant.Slug, context.Value.Mailbox.Address, folder.Name, moved.MaildirFile, seen: false, flagged: moved.Flagged, answered: moved.Answered);
            await this.store.SetMessageFlagsAsync(moved.Id, seen: false, flagged: moved.Flagged, answered: moved.Answered, file ?? moved.MaildirFile, ct).ConfigureAwait(false);
        }
        doc.Woken[moved.Id] = DateTimeOffset.UtcNow;
    }

    /// <summary>The mail woken in the last day that is in a folder, newest wake first, for the top of its list.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="folderId">The folder.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rows.</returns>
    public async Task<IReadOnlyList<MessageRow>> WokenRowsAsync(Guid mailboxId, Guid folderId, CancellationToken ct = default)
    {
        SnoozeDocument doc = await this.SnoozeOfAsync(mailboxId, ct).ConfigureAwait(false);
        DateTimeOffset since = DateTimeOffset.UtcNow.AddDays(-1);
        var rows = new List<MessageRow>();
        foreach (Guid id in doc.Woken.Where(kv => kv.Value >= since).OrderByDescending(kv => kv.Value).Select(kv => kv.Key))
        {
            MessageRow? row = await this.GetOwnedRowAsync(mailboxId, id, ct).ConfigureAwait(false);
            if (row is not null && row.FolderId == folderId)
            {
                rows.Add(row);
            }
        }
        return rows;
    }
}
