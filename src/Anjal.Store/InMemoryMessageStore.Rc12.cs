namespace Anjal.Store;

/// <summary>
/// rc.12 additions to the in-memory store: a mailbox's own documents
/// (contacts, groups, templates, rules) and the Outbox.
/// </summary>
public sealed partial class InMemoryMessageStore
{
    private readonly Dictionary<(System.Guid, string), string> documents = new();

    /// <inheritdoc/>
    public Task<bool> DeleteEmptyFolderAsync(System.Guid mailboxId, System.Guid folderId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            if (this.mailboxMessages.Any(m => m.FolderId == folderId))
            {
                return Task.FromResult(false);
            }
            return Task.FromResult(this.folders.RemoveAll(f => f.Id == folderId && f.MailboxId == mailboxId) > 0);
        }
    }

    /// <inheritdoc/>
    public Task<string?> GetMailboxDocumentAsync(System.Guid mailboxId, string kind, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(kind);
        lock (this.gate)
        {
            return Task.FromResult(this.documents.TryGetValue((mailboxId, kind), out string? json) ? json : null);
        }
    }

    /// <inheritdoc/>
    public Task SetMailboxDocumentAsync(System.Guid mailboxId, string kind, string json, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(kind);
        System.ArgumentNullException.ThrowIfNull(json);
        lock (this.gate)
        {
            this.documents[(mailboxId, kind)] = json;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<OutboundMessage>> ListOutboundForSenderAsync(string envelopeFrom, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(envelopeFrom);
        lock (this.gate)
        {
            IReadOnlyList<OutboundMessage> found = this.outbound
                .Where(m => string.Equals(m.EnvelopeFrom, envelopeFrom, System.StringComparison.OrdinalIgnoreCase)
                    && (m.Status == OutboundStatus.Pending || m.Status == OutboundStatus.Sending))
                .OrderBy(m => m.CreatedAt)
                .ToArray();
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc/>
    public Task<bool> CancelOutboundAsync(System.Guid id, string envelopeFrom, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(envelopeFrom);
        lock (this.gate)
        {
            OutboundMessage? found = this.outbound.Find(m => m.Id == id && m.Status == OutboundStatus.Pending
                && string.Equals(m.EnvelopeFrom, envelopeFrom, System.StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                return Task.FromResult(false);
            }
            found.Status = OutboundStatus.Failed;
            found.LastError = OutboundMessage.CancelledBySender;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc/>
    public Task<bool> RetryOutboundNowAsync(System.Guid id, string envelopeFrom, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(envelopeFrom);
        lock (this.gate)
        {
            OutboundMessage? found = this.outbound.Find(m => m.Id == id && m.Status == OutboundStatus.Pending
                && string.Equals(m.EnvelopeFrom, envelopeFrom, System.StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                return Task.FromResult(false);
            }
            found.NextAttemptAt = System.DateTimeOffset.UtcNow;
            return Task.FromResult(true);
        }
    }
}
