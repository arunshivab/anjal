namespace Anjal.Store;

/// <summary>rc.26 additions to the in-memory store.</summary>
public sealed partial class InMemoryMessageStore
{
    /// <inheritdoc/>
    public Task<long> CountListMailAsync(System.Guid mailboxId, string listMark, System.DateTimeOffset since, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(listMark);
        lock (this.gate)
        {
            return Task.FromResult(listMark.Length == 0 ? 0L : this.mailboxMessages.LongCount(m => m.MailboxId == mailboxId && m.ListMark == listMark && m.ReceivedAt >= since));
        }
    }

    /// <inheritdoc/>
    public Task TouchSignInAsync(System.Guid mailboxId, System.DateTimeOffset at, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            if (this.mailboxes.Find(m => m.Id == mailboxId) is MailboxRow m && m.LastSignInAt < at)
            {
                m.LastSignInAt = at;
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>Tests: set a mailbox's last sign-in to any moment.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="at">When.</param>
    public void SetLastSignIn(System.Guid mailboxId, System.DateTimeOffset at)
    {
        lock (this.gate)
        {
            if (this.mailboxes.Find(m => m.Id == mailboxId) is MailboxRow m)
            {
                m.LastSignInAt = at;
            }
        }
    }

    // A deleted mailbox's keys, grants (either way), session copies and restore request (called under the gate).
    private void RemoveMailboxKeys(System.Guid mailboxId)
    {
        this.mailboxKeys.Remove(mailboxId);
        foreach ((System.Guid, System.Guid, string) key in this.keyGrants.Keys.Where(k => k.Owner == mailboxId || k.Holder == mailboxId).ToList())
        {
            this.keyGrants.Remove(key);
        }
        foreach (string sid in this.sessionKeys.Where(k => k.Value.Mailbox == mailboxId).Select(k => k.Key).ToList())
        {
            this.sessionKeys.Remove(sid);
        }
        this.restoreRequests.Remove(mailboxId);
    }
}
