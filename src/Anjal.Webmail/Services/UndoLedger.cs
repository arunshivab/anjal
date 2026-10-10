namespace Anjal.Webmail.Services;

/// <summary>
/// Remembers, for ten minutes, where messages were before a delete to Trash or
/// a move, so the person can put them back with one Undo (rc.11, UX-07). A
/// record belongs to one mailbox and can be used once. "Now" is always passed
/// in, so the result never depends on when it runs.
/// </summary>
public sealed class UndoLedger
{
    /// <summary>How long an Undo stays available.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly object gate = new();
    private readonly Dictionary<Guid, Entry> entries = new();

    /// <summary>Note where each message was, before it is moved.</summary>
    /// <param name="mailboxId">The mailbox the messages belong to.</param>
    /// <param name="moves">Each message and the folder it was in.</param>
    /// <param name="now">The present moment.</param>
    /// <returns>The token that undoes this move.</returns>
    public Guid Record(Guid mailboxId, IReadOnlyList<(Guid MessageId, string FromFolder)> moves, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(moves);
        Guid token = Guid.NewGuid();
        lock (this.gate)
        {
            foreach (Guid stale in this.entries.Where(e => now - e.Value.At > Lifetime).Select(e => e.Key).ToList())
            {
                this.entries.Remove(stale);
            }
            this.entries[token] = new Entry(mailboxId, moves.ToList(), now);
        }
        return token;
    }

    /// <summary>
    /// Take a record to undo it: only by the mailbox that made it, only once,
    /// and only within <see cref="Lifetime"/>.
    /// </summary>
    /// <param name="mailboxId">The mailbox asking.</param>
    /// <param name="token">The token from <see cref="Record"/>.</param>
    /// <param name="now">The present moment.</param>
    /// <returns>Each message and the folder to put it back in; empty when not available.</returns>
    public IReadOnlyList<(Guid MessageId, string FromFolder)> Take(Guid mailboxId, Guid token, DateTimeOffset now)
    {
        lock (this.gate)
        {
            if (!this.entries.TryGetValue(token, out Entry? entry) || entry.MailboxId != mailboxId)
            {
                return Array.Empty<(Guid, string)>();
            }
            this.entries.Remove(token);
            return now - entry.At > Lifetime ? Array.Empty<(Guid, string)>() : entry.Moves;
        }
    }

    private sealed record Entry(Guid MailboxId, List<(Guid MessageId, string FromFolder)> Moves, DateTimeOffset At);
}
