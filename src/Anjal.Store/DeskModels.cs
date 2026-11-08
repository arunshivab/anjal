namespace Anjal.Store;

/// <summary>
/// rc.27 (DES-17 §2): a conversation on a shared desk that someone has acted on. Whether it is
/// Open, Waiting or Not assigned follows from its mail (who wrote last); the row keeps only who has
/// it and when it was marked done.
/// </summary>
public sealed class DeskItemRow
{
    /// <summary>Its id.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The desk (a shared mailbox).</summary>
    public System.Guid DeskId { get; set; }

    /// <summary>The conversation's keyed mark: never its subject.</summary>
    public string ThreadKey { get; set; } = string.Empty;

    /// <summary>A mail of the conversation, to open it from elsewhere (Today).</summary>
    public System.Guid? RootMessageId { get; set; }

    /// <summary>The member who has it, or null.</summary>
    public System.Guid? AssigneeId { get; set; }

    /// <summary>When it was marked done; null while it is not.</summary>
    public System.DateTimeOffset? DoneAt { get; set; }

    /// <summary>When the oldest mail the desk has not answered arrived; null when the desk wrote last.</summary>
    public System.DateTimeOffset? PendingSince { get; set; }

    /// <summary>When the desk last replied in it.</summary>
    public System.DateTimeOffset? LastReplyAt { get; set; }

    /// <summary>When the row was made.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }

    /// <summary>When it last changed.</summary>
    public System.DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One line of a desk item's history (rc.27): who did what and when, never the mail's words.</summary>
public sealed class DeskEventRow
{
    /// <summary>Its number, in order.</summary>
    public long Id { get; set; }

    /// <summary>The item.</summary>
    public System.Guid ItemId { get; set; }

    /// <summary>When.</summary>
    public System.DateTimeOffset At { get; set; }

    /// <summary>Who did it; null for Anjal (a mail arrived).</summary>
    public System.Guid? ActorId { get; set; }

    /// <summary>assigned, unassigned, taken, returned, replied, done, reopened, note, note-withdrawn.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The other person's id for an assignment, the note's id for a note; otherwise empty.</summary>
    public string Detail { get; set; } = string.Empty;
}

/// <summary>A note for a desk, never sent (rc.27, DES-17 §2.8).</summary>
public sealed class DeskNoteRow
{
    /// <summary>Its id.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The item.</summary>
    public System.Guid ItemId { get; set; }

    /// <summary>When it was written.</summary>
    public System.DateTimeOffset At { get; set; }

    /// <summary>Who wrote it.</summary>
    public System.Guid? WriterId { get; set; }

    /// <summary>The text, sealed to the desk's key (plain only for a desk that is not encrypted).</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>The members it mentions, as ids separated by commas.</summary>
    public string Mentions { get; set; } = string.Empty;

    /// <summary>When the writer withdrew it; null while it stands.</summary>
    public System.DateTimeOffset? WithdrawnAt { get; set; }
}

/// <summary>rc.27: what shared desks and the draft clean-up need from the store.</summary>
public interface IDeskStore
{
    /// <summary>The item for a conversation on a desk, made when there is none (two at once get the same row).</summary>
    /// <param name="deskId">The desk.</param>
    /// <param name="threadKey">The conversation's mark.</param>
    /// <param name="rootMessageId">A mail of the conversation.</param>
    /// <param name="pendingSince">For a new row: when its oldest unanswered mail arrived, or null.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The item.</returns>
    System.Threading.Tasks.Task<DeskItemRow> GetOrAddDeskItemAsync(System.Guid deskId, string threadKey, System.Guid? rootMessageId, System.DateTimeOffset? pendingSince, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// A mail arrived on a desk: the item is made, or a done one reopened (keeping its person), and
    /// its unanswered time starts if it had none.
    /// </summary>
    /// <param name="deskId">The desk.</param>
    /// <param name="threadKey">The conversation's mark.</param>
    /// <param name="messageId">The mail.</param>
    /// <param name="at">When it arrived.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The item, and true when it had been done.</returns>
    System.Threading.Tasks.Task<(DeskItemRow Item, bool Reopened)> NoteDeskArrivalAsync(System.Guid deskId, string threadKey, System.Guid messageId, System.DateTimeOffset at, System.Threading.CancellationToken ct = default);

    /// <summary>The desk replied in a conversation: nothing is owed until the next mail arrives.</summary>
    /// <param name="deskId">The desk.</param>
    /// <param name="threadKey">The conversation's mark.</param>
    /// <param name="messageId">The reply's Sent copy, if kept.</param>
    /// <param name="at">When.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The item, and since when a reply had been owed (null when nothing was).</returns>
    System.Threading.Tasks.Task<(DeskItemRow Item, System.DateTimeOffset? WasPending)> NoteDeskReplyAsync(System.Guid deskId, string threadKey, System.Guid? messageId, System.DateTimeOffset at, System.Threading.CancellationToken ct = default);

    /// <summary>One item by its id.</summary>
    /// <param name="itemId">The item.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The item, or null.</returns>
    System.Threading.Tasks.Task<DeskItemRow?> GetDeskItemAsync(System.Guid itemId, System.Threading.CancellationToken ct = default);

    /// <summary>A desk's items.</summary>
    /// <param name="deskId">The desk.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The items.</returns>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<DeskItemRow>> ListDeskItemsAsync(System.Guid deskId, System.Threading.CancellationToken ct = default);

    /// <summary>The items assigned to a person and not done, on every desk.</summary>
    /// <param name="assigneeId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The items.</returns>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<DeskItemRow>> ListAssignedDeskItemsAsync(System.Guid assigneeId, System.Threading.CancellationToken ct = default);

    /// <summary>Set who has an item, and nothing else: an arrival or a "done" at the same moment is kept.</summary>
    /// <param name="itemId">The item.</param>
    /// <param name="assigneeId">Who has it, or null.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when the item exists.</returns>
    System.Threading.Tasks.Task<bool> SetDeskAssigneeAsync(System.Guid itemId, System.Guid? assigneeId, System.Threading.CancellationToken ct = default);

    /// <summary>Mark an item done, or open again, and nothing else: an assignment at the same moment is kept.</summary>
    /// <param name="itemId">The item.</param>
    /// <param name="doneAt">When it was done, or null to open it again.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when the item exists.</returns>
    System.Threading.Tasks.Task<bool> SetDeskDoneAsync(System.Guid itemId, System.DateTimeOffset? doneAt, System.Threading.CancellationToken ct = default);

    /// <summary>Add a line to an item's history.</summary>
    /// <param name="row">The line (its id and time are set here when empty).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    System.Threading.Tasks.Task AddDeskEventAsync(DeskEventRow row, System.Threading.CancellationToken ct = default);

    /// <summary>An item's history, oldest first.</summary>
    /// <param name="itemId">The item.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The lines.</returns>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<DeskEventRow>> ListDeskEventsAsync(System.Guid itemId, System.Threading.CancellationToken ct = default);

    /// <summary>A desk's history since a moment, every item, oldest first (for its figures).</summary>
    /// <param name="deskId">The desk.</param>
    /// <param name="since">From when.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The lines.</returns>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<DeskEventRow>> ListDeskEventsSinceAsync(System.Guid deskId, System.DateTimeOffset since, System.Threading.CancellationToken ct = default);

    /// <summary>Add a note.</summary>
    /// <param name="row">The note (its id and time are set here when empty).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    System.Threading.Tasks.Task AddDeskNoteAsync(DeskNoteRow row, System.Threading.CancellationToken ct = default);

    /// <summary>An item's notes, oldest first, withdrawn ones included.</summary>
    /// <param name="itemId">The item.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The notes.</returns>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<DeskNoteRow>> ListDeskNotesAsync(System.Guid itemId, System.Threading.CancellationToken ct = default);

    /// <summary>Withdraw a note of one item; only its writer may.</summary>
    /// <param name="noteId">The note.</param>
    /// <param name="itemId">The item it must belong to.</param>
    /// <param name="writerId">Who asks.</param>
    /// <param name="at">When.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when it was withdrawn now.</returns>
    System.Threading.Tasks.Task<bool> WithdrawDeskNoteAsync(System.Guid noteId, System.Guid itemId, System.Guid writerId, System.DateTimeOffset at, System.Threading.CancellationToken ct = default);

    /// <summary>Drafts saved empty and untouched since a moment, on every mailbox (D-309).</summary>
    /// <param name="before">Saved before this.</param>
    /// <param name="limit">At most this many.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The drafts.</returns>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<MessageRow>> ListEmptyDraftsAsync(System.DateTimeOffset before, int limit, System.Threading.CancellationToken ct = default);
}
