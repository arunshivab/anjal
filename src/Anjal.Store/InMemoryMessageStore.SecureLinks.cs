namespace Anjal.Store;

/// <summary>rc.28 additions to the in-memory store: secure links (DES-19).</summary>
public sealed partial class InMemoryMessageStore : ISecureLinkStore
{
    private readonly List<SecureLinkRow> secureLinks = new();
    private readonly List<SecureLinkCodeRow> secureLinkCodes = new();

    /// <inheritdoc/>
    public Task<SecureLinkRow> AddSecureLinkAsync(SecureLinkRow link, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(link);
        lock (this.gate)
        {
            SecureLinkRow copy = CloneLink(link);
            if (copy.Id == System.Guid.Empty)
            {
                copy.Id = System.Guid.NewGuid();
            }
            this.secureLinks.Add(copy);
            return Task.FromResult(CloneLink(copy));
        }
    }

    /// <inheritdoc/>
    public Task<SecureLinkRow?> GetSecureLinkByTokenAsync(string tokenSha256, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(tokenSha256);
        lock (this.gate)
        {
            SecureLinkRow? found = this.secureLinks.FirstOrDefault(l => string.Equals(l.TokenSha256, tokenSha256, System.StringComparison.Ordinal));
            return Task.FromResult(found is null ? null : CloneLink(found));
        }
    }

    /// <inheritdoc/>
    public Task<SecureLinkRow?> GetSecureLinkAsync(System.Guid id, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            SecureLinkRow? found = this.secureLinks.FirstOrDefault(l => l.Id == id);
            return Task.FromResult(found is null ? null : CloneLink(found));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SecureLinkRow>> ListSecureLinksForMessageAsync(System.Guid mailboxId, string messageId, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(messageId);
        lock (this.gate)
        {
            IReadOnlyList<SecureLinkRow> list = this.secureLinks
                .Where(l => l.MailboxId == mailboxId && string.Equals(l.MessageId, messageId, System.StringComparison.Ordinal))
                .OrderBy(l => l.CreatedAt).ThenBy(l => l.Recipient, System.StringComparer.Ordinal)
                .Select(CloneLink).ToList();
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc/>
    public Task<SecureLinkRow?> UpdateSecureLinkAsync(System.Guid id, SecureLinkChange change, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(change);
        lock (this.gate)
        {
            SecureLinkRow? l = this.secureLinks.FirstOrDefault(x => x.Id == id);
            if (l is null)
            {
                return Task.FromResult<SecureLinkRow?>(null);
            }
            l.StoppedAt ??= change.StoppedAt;
            l.LockedAt ??= change.LockedAt;
            l.MailedAt = change.MailedAt ?? l.MailedAt;
            l.LastCodeAt = change.LastCodeAt ?? l.LastCodeAt;
            l.OpenedAt ??= change.OpenedAt;
            l.RepliedAt = change.RepliedAt ?? l.RepliedAt;
            l.WrongCodes += change.AddWrongCodes;
            l.Replies += change.AddReplies;
            return Task.FromResult<SecureLinkRow?>(CloneLink(l));
        }
    }

    /// <inheritdoc/>
    public Task<int?> TakeSecureCodeTryAsync(System.Guid id, int most, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            SecureLinkRow? l = this.secureLinks.FirstOrDefault(x => x.Id == id);
            if (l is null || l.LockedAt is not null || l.StoppedAt is not null || l.WrongCodes >= most)
            {
                return Task.FromResult<int?>(null);
            }
            l.WrongCodes++;
            return Task.FromResult<int?>(l.WrongCodes);
        }
    }

    /// <inheritdoc/>
    public Task<bool> TakeSecureReplyAsync(System.Guid id, System.DateTimeOffset visitStart, System.DateTimeOffset now, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            SecureLinkRow? l = this.secureLinks.FirstOrDefault(x => x.Id == id);
            if (l is null || (l.RepliedAt is System.DateTimeOffset last && last >= visitStart))
            {
                return Task.FromResult(false);
            }
            l.RepliedAt = now;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc/>
    public Task<bool> TakeSecureCodeMailAsync(System.Guid id, System.DateTimeOffset now, System.DateTimeOffset gapStart, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            SecureLinkRow? l = this.secureLinks.FirstOrDefault(x => x.Id == id);
            if (l is null || (l.LastCodeAt is System.DateTimeOffset last && last >= gapStart))
            {
                return Task.FromResult(false);
            }
            l.LastCodeAt = now;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc/>
    public Task AddSecureLinkCodeAsync(SecureLinkCodeRow code, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(code);
        lock (this.gate)
        {
            this.secureLinkCodes.Add(new SecureLinkCodeRow
            {
                Id = code.Id == System.Guid.Empty ? System.Guid.NewGuid() : code.Id,
                LinkId = code.LinkId,
                CodeHmac = code.CodeHmac,
                MadeAt = code.MadeAt,
                UsedAt = code.UsedAt,
            });
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SecureLinkCodeRow>> ListSecureLinkCodesAsync(System.Guid linkId, System.DateTimeOffset since, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<SecureLinkCodeRow> list = this.secureLinkCodes
                .Where(c => c.LinkId == linkId && c.MadeAt >= since)
                .OrderByDescending(c => c.MadeAt)
                .Select(c => new SecureLinkCodeRow { Id = c.Id, LinkId = c.LinkId, CodeHmac = c.CodeHmac, MadeAt = c.MadeAt, UsedAt = c.UsedAt })
                .ToList();
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc/>
    public Task<bool> UseSecureLinkCodeAsync(System.Guid codeId, System.DateTimeOffset at, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            SecureLinkCodeRow? c = this.secureLinkCodes.FirstOrDefault(x => x.Id == codeId);
            if (c is null || c.UsedAt is not null)
            {
                return Task.FromResult(false);
            }
            c.UsedAt = at;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc/>
    public Task<int> PurgeEndedSecureLinksAsync(System.DateTimeOffset endedBefore, System.DateTimeOffset now, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            int n = 0;
            foreach (SecureLinkRow l in this.secureLinks.Where(l => l.PurgedAt is null))
            {
                System.DateTimeOffset ended = l.StoppedAt ?? l.LockedAt ?? l.ExpiresAt;
                if (ended < endedBefore)
                {
                    l.Sealed = System.Array.Empty<byte>();
                    l.PurgedAt = now;
                    n++;
                }
            }
            this.secureLinkCodes.RemoveAll(c => this.secureLinks.Any(l => l.Id == c.LinkId && l.PurgedAt is not null));
            return Task.FromResult(n);
        }
    }

    private static SecureLinkRow CloneLink(SecureLinkRow l) => new()
    {
        Id = l.Id,
        MailboxId = l.MailboxId,
        SentBy = l.SentBy,
        Recipient = l.Recipient,
        TokenSha256 = l.TokenSha256,
        Sealed = l.Sealed,
        MessageId = l.MessageId,
        SentCopyId = l.SentCopyId,
        CreatedAt = l.CreatedAt,
        ExpiresAt = l.ExpiresAt,
        StoppedAt = l.StoppedAt,
        LockedAt = l.LockedAt,
        MailedAt = l.MailedAt,
        LastCodeAt = l.LastCodeAt,
        OpenedAt = l.OpenedAt,
        RepliedAt = l.RepliedAt,
        WrongCodes = l.WrongCodes,
        Replies = l.Replies,
        PurgedAt = l.PurgedAt,
    };
}
