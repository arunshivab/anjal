namespace Anjal.Store;

/// <summary>rc.27 additions to the in-memory store: shared desks and the draft clean-up.</summary>
public sealed partial class InMemoryMessageStore
{
    private readonly List<DeskItemRow> deskItems = new();
    private readonly List<DeskEventRow> deskEvents = new();
    private readonly List<DeskNoteRow> deskNotes = new();
    private long deskEventSeq;

    /// <inheritdoc/>
    public Task<DeskItemRow> GetOrAddDeskItemAsync(System.Guid deskId, string threadKey, System.Guid? rootMessageId, System.DateTimeOffset? pendingSince, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(threadKey);
        lock (this.gate)
        {
            return Task.FromResult(CloneItem(this.DeskItemOf(deskId, threadKey, rootMessageId, pendingSince)));
        }
    }

    /// <inheritdoc/>
    public Task<(DeskItemRow Item, bool Reopened)> NoteDeskArrivalAsync(System.Guid deskId, string threadKey, System.Guid messageId, System.DateTimeOffset at, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(threadKey);
        lock (this.gate)
        {
            DeskItemRow item = this.DeskItemOf(deskId, threadKey, messageId, at);
            bool reopened = item.DoneAt is not null;
            if (reopened || item.PendingSince is null)
            {
                item.PendingSince = at;
            }
            item.DoneAt = null;
            item.UpdatedAt = this.MessageClock();
            return Task.FromResult((CloneItem(item), reopened));
        }
    }

    /// <inheritdoc/>
    public Task<(DeskItemRow Item, System.DateTimeOffset? WasPending)> NoteDeskReplyAsync(System.Guid deskId, string threadKey, System.Guid? messageId, System.DateTimeOffset at, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(threadKey);
        lock (this.gate)
        {
            DeskItemRow item = this.DeskItemOf(deskId, threadKey, messageId, null);
            System.DateTimeOffset? was = item.PendingSince;
            item.PendingSince = null;
            item.LastReplyAt = at;
            item.UpdatedAt = this.MessageClock();
            return Task.FromResult((CloneItem(item), was));
        }
    }

    // Called under the gate.
    private DeskItemRow DeskItemOf(System.Guid deskId, string threadKey, System.Guid? rootMessageId, System.DateTimeOffset? pendingSince)
    {
        DeskItemRow? found = this.deskItems.Find(i => i.DeskId == deskId && i.ThreadKey == threadKey);
        if (found is null)
        {
            System.DateTimeOffset now = this.MessageClock();
            found = new DeskItemRow { Id = System.Guid.NewGuid(), DeskId = deskId, ThreadKey = threadKey, RootMessageId = rootMessageId, PendingSince = pendingSince, CreatedAt = now, UpdatedAt = now };
            this.deskItems.Add(found);
        }
        return found;
    }

    /// <inheritdoc/>
    public Task<DeskItemRow?> GetDeskItemAsync(System.Guid itemId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            DeskItemRow? found = this.deskItems.Find(i => i.Id == itemId);
            return Task.FromResult(found is null ? null : CloneItem(found));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<DeskItemRow>> ListDeskItemsAsync(System.Guid deskId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            return Task.FromResult<IReadOnlyList<DeskItemRow>>(this.deskItems.Where(i => i.DeskId == deskId).Select(CloneItem).ToList());
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<DeskItemRow>> ListAssignedDeskItemsAsync(System.Guid assigneeId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            return Task.FromResult<IReadOnlyList<DeskItemRow>>(this.deskItems.Where(i => i.AssigneeId == assigneeId && i.DoneAt is null).Select(CloneItem).ToList());
        }
    }

    /// <inheritdoc/>
    public Task<bool> SetDeskAssigneeAsync(System.Guid itemId, System.Guid? assigneeId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            DeskItemRow? found = this.deskItems.Find(i => i.Id == itemId);
            if (found is null)
            {
                return Task.FromResult(false);
            }
            found.AssigneeId = assigneeId;
            found.UpdatedAt = this.MessageClock();
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc/>
    public Task<bool> SetDeskDoneAsync(System.Guid itemId, System.DateTimeOffset? doneAt, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            DeskItemRow? found = this.deskItems.Find(i => i.Id == itemId);
            if (found is null)
            {
                return Task.FromResult(false);
            }
            found.DoneAt = doneAt;
            found.UpdatedAt = this.MessageClock();
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc/>
    public Task AddDeskEventAsync(DeskEventRow row, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(row);
        lock (this.gate)
        {
            if (!this.deskItems.Exists(i => i.Id == row.ItemId))
            {
                return Task.CompletedTask;
            }
            this.deskEvents.Add(new DeskEventRow
            {
                Id = ++this.deskEventSeq,
                ItemId = row.ItemId,
                At = row.At == default ? this.MessageClock() : row.At,
                ActorId = row.ActorId,
                Kind = row.Kind,
                Detail = row.Detail,
            });
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<DeskEventRow>> ListDeskEventsAsync(System.Guid itemId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            return Task.FromResult<IReadOnlyList<DeskEventRow>>(this.deskEvents.Where(e => e.ItemId == itemId).OrderBy(e => e.At).ThenBy(e => e.Id).Select(CloneEvent).ToList());
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<DeskEventRow>> ListDeskEventsSinceAsync(System.Guid deskId, System.DateTimeOffset since, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            var items = this.deskItems.Where(i => i.DeskId == deskId).Select(i => i.Id).ToHashSet();
            return Task.FromResult<IReadOnlyList<DeskEventRow>>(this.deskEvents.Where(e => items.Contains(e.ItemId) && e.At >= since).OrderBy(e => e.At).ThenBy(e => e.Id).Select(CloneEvent).ToList());
        }
    }

    /// <inheritdoc/>
    public Task AddDeskNoteAsync(DeskNoteRow row, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(row);
        lock (this.gate)
        {
            if (!this.deskItems.Exists(i => i.Id == row.ItemId))
            {
                return Task.CompletedTask;
            }
            if (row.Id == System.Guid.Empty)
            {
                row.Id = System.Guid.NewGuid();
            }
            if (row.At == default)
            {
                row.At = this.MessageClock();
            }
            this.deskNotes.Add(CloneNote(row));
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<DeskNoteRow>> ListDeskNotesAsync(System.Guid itemId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            return Task.FromResult<IReadOnlyList<DeskNoteRow>>(this.deskNotes.Where(n => n.ItemId == itemId).OrderBy(n => n.At).Select(CloneNote).ToList());
        }
    }

    /// <inheritdoc/>
    public Task<bool> WithdrawDeskNoteAsync(System.Guid noteId, System.Guid itemId, System.Guid writerId, System.DateTimeOffset at, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            DeskNoteRow? note = this.deskNotes.Find(n => n.Id == noteId && n.ItemId == itemId);
            if (note is null || note.WriterId != writerId || note.WithdrawnAt is not null)
            {
                return Task.FromResult(false);
            }
            note.WithdrawnAt = at;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<MessageRow>> ListEmptyDraftsAsync(System.DateTimeOffset before, int limit, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            var drafts = this.folders.Where(f => string.Equals(f.Name, "Drafts", System.StringComparison.Ordinal)).Select(f => f.Id).ToHashSet();
            return Task.FromResult<IReadOnlyList<MessageRow>>(this.mailboxMessages
                .Where(m => m.DraftEmpty && m.ReceivedAt < before && drafts.Contains(m.FolderId))
                .OrderBy(m => m.ReceivedAt)
                .Take(limit)
                .Select(Clone)
                .ToList());
        }
    }

    // Called under the gate when a mailbox goes (the database's ON DELETE CASCADE and SET NULL).
    private void RemoveDeskOf(System.Guid mailboxId)
    {
        var gone = this.deskItems.Where(i => i.DeskId == mailboxId).Select(i => i.Id).ToHashSet();
        this.deskItems.RemoveAll(i => gone.Contains(i.Id));
        this.deskEvents.RemoveAll(e => gone.Contains(e.ItemId));
        this.deskNotes.RemoveAll(n => gone.Contains(n.ItemId));
        foreach (DeskItemRow held in this.deskItems.Where(i => i.AssigneeId == mailboxId))
        {
            held.AssigneeId = null;
        }
    }

    private static DeskItemRow CloneItem(DeskItemRow i) => new()
    {
        Id = i.Id,
        DeskId = i.DeskId,
        ThreadKey = i.ThreadKey,
        RootMessageId = i.RootMessageId,
        AssigneeId = i.AssigneeId,
        DoneAt = i.DoneAt,
        PendingSince = i.PendingSince,
        LastReplyAt = i.LastReplyAt,
        CreatedAt = i.CreatedAt,
        UpdatedAt = i.UpdatedAt,
    };

    private static DeskEventRow CloneEvent(DeskEventRow e) => new()
    {
        Id = e.Id,
        ItemId = e.ItemId,
        At = e.At,
        ActorId = e.ActorId,
        Kind = e.Kind,
        Detail = e.Detail,
    };

    private static DeskNoteRow CloneNote(DeskNoteRow n) => new()
    {
        Id = n.Id,
        ItemId = n.ItemId,
        At = n.At,
        WriterId = n.WriterId,
        Body = n.Body,
        Mentions = n.Mentions,
        WithdrawnAt = n.WithdrawnAt,
    };
}
