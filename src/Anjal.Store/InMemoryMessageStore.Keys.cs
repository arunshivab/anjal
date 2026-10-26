namespace Anjal.Store;

/// <summary>rc.25 (DES-15 §10): the key tables in memory (tests and development).</summary>
public sealed partial class InMemoryMessageStore : IMailKeyStore
{
    private readonly Dictionary<Guid, MailboxKeyRow> mailboxKeys = new();
    private readonly Dictionary<Guid, TenantKeyRow> tenantKeys = new();
    private readonly Dictionary<(Guid Owner, Guid Holder, string Kind), KeyGrant> keyGrants = new();
    private readonly Dictionary<string, (Guid Mailbox, byte[] Body, DateTimeOffset Expires)> sessionKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, RestoreRequestRow> restoreRequests = new();
    private BreakGlassRow? breakGlass;

    /// <inheritdoc/>
    public Task<MailboxKeyRow?> GetMailboxKeysAsync(Guid mailboxId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            return Task.FromResult(this.mailboxKeys.TryGetValue(mailboxId, out MailboxKeyRow? row) ? Copy(row) : null);
        }
    }

    /// <inheritdoc/>
    public Task SaveMailboxKeysAsync(MailboxKeyRow row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (this.gate)
        {
            MailboxKeyRow copy = Copy(row);
            if (this.mailboxKeys.TryGetValue(row.MailboxId, out MailboxKeyRow? old) && !old.PublicKey.AsSpan().SequenceEqual(row.PublicKey))
            {
                throw new InvalidOperationException("A mailbox's public key never changes.");
            }
            copy.CreatedAt = old?.CreatedAt ?? DateTimeOffset.UtcNow;
            copy.UpdatedAt = DateTimeOffset.UtcNow;
            this.mailboxKeys[row.MailboxId] = copy;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> AddMailboxKeysAsync(MailboxKeyRow row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (this.gate)
        {
            if (this.mailboxKeys.ContainsKey(row.MailboxId))
            {
                return Task.FromResult(false);
            }
            MailboxKeyRow copy = Copy(row);
            copy.CreatedAt = DateTimeOffset.UtcNow;
            copy.UpdatedAt = copy.CreatedAt;
            this.mailboxKeys[row.MailboxId] = copy;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<Guid, byte[]>> GetPublicKeysAsync(IReadOnlyCollection<Guid> mailboxIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mailboxIds);
        lock (this.gate)
        {
            IReadOnlyDictionary<Guid, byte[]> result = mailboxIds.Distinct()
                .Where(this.mailboxKeys.ContainsKey)
                .ToDictionary(id => id, id => this.mailboxKeys[id].PublicKey);
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc/>
    public Task<TenantKeyRow?> GetTenantKeysAsync(Guid tenantId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            return Task.FromResult(this.tenantKeys.TryGetValue(tenantId, out TenantKeyRow? row) ? Copy(row) : null);
        }
    }

    /// <inheritdoc/>
    public Task SaveTenantKeysAsync(TenantKeyRow row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (this.gate)
        {
            TenantKeyRow copy = Copy(row);
            if (this.tenantKeys.TryGetValue(row.TenantId, out TenantKeyRow? old) && !old.RecoveryPublic.AsSpan().SequenceEqual(row.RecoveryPublic))
            {
                throw new InvalidOperationException("An organisation's recovery public key never changes.");
            }
            copy.CreatedAt = old?.CreatedAt ?? DateTimeOffset.UtcNow;
            copy.UpdatedAt = DateTimeOffset.UtcNow;
            this.tenantKeys[row.TenantId] = copy;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> AddTenantKeysAsync(TenantKeyRow row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (this.gate)
        {
            if (this.tenantKeys.ContainsKey(row.TenantId))
            {
                return Task.FromResult(false);
            }
            TenantKeyRow copy = Copy(row);
            copy.CreatedAt = DateTimeOffset.UtcNow;
            copy.UpdatedAt = copy.CreatedAt;
            this.tenantKeys[row.TenantId] = copy;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<KeyGrant>> ListGrantsForHolderAsync(Guid holderId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<KeyGrant> rows = this.keyGrants.Values.Where(g => g.HolderId == holderId).ToList();
            return Task.FromResult(rows);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<KeyGrant>> ListGrantsOfAsync(Guid keyOwner, string kind, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(kind);
        lock (this.gate)
        {
            IReadOnlyList<KeyGrant> rows = this.keyGrants.Values.Where(g => g.KeyOwner == keyOwner && g.Kind == kind).ToList();
            return Task.FromResult(rows);
        }
    }

    /// <inheritdoc/>
    public Task SaveGrantAsync(KeyGrant grant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        lock (this.gate)
        {
            this.keyGrants[(grant.KeyOwner, grant.HolderId, grant.Kind)] = grant;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DeleteGrantAsync(Guid keyOwner, Guid holderId, string kind, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(kind);
        lock (this.gate)
        {
            this.keyGrants.Remove((keyOwner, holderId, kind));
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SaveSessionKeysAsync(string sessionId, Guid mailboxId, byte[] body, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(body);
        lock (this.gate)
        {
            this.sessionKeys[sessionId] = (mailboxId, (byte[])body.Clone(), expiresAt);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<byte[]?> GetSessionKeysAsync(string sessionId, Guid mailboxId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        lock (this.gate)
        {
            return Task.FromResult(this.sessionKeys.TryGetValue(sessionId, out var s) && s.Mailbox == mailboxId && s.Expires > DateTimeOffset.UtcNow ? (byte[]?)s.Body : null);
        }
    }

    /// <inheritdoc/>
    public Task DeleteSessionKeysAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        lock (this.gate)
        {
            this.sessionKeys.Remove(sessionId);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<int> DeleteMailboxSessionKeysAsync(Guid mailboxId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            List<string> gone = this.sessionKeys.Where(s => s.Value.Mailbox == mailboxId).Select(s => s.Key).ToList();
            gone.ForEach(k => this.sessionKeys.Remove(k));
            return Task.FromResult(gone.Count);
        }
    }

    /// <inheritdoc/>
    public Task<int> PurgeSessionKeysAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            List<string> gone = this.sessionKeys.Where(s => s.Value.Expires <= now).Select(s => s.Key).ToList();
            gone.ForEach(k => this.sessionKeys.Remove(k));
            return Task.FromResult(gone.Count);
        }
    }

    /// <inheritdoc/>
    public Task<RestoreRequestRow?> GetRestoreRequestAsync(Guid mailboxId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            return Task.FromResult(this.restoreRequests.TryGetValue(mailboxId, out RestoreRequestRow? row) ? Copy(row) : null);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<RestoreRequestRow>> ListWaitingRestoresAsync(Guid tenantId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            var mine = new HashSet<Guid>(this.mailboxes.Where(m => m.TenantId == tenantId).Select(m => m.Id));
            IReadOnlyList<RestoreRequestRow> rows = this.restoreRequests.Values.Where(r => mine.Contains(r.MailboxId) && r.Granted is null).OrderBy(r => r.CreatedAt).Select(Copy).ToList();
            return Task.FromResult(rows);
        }
    }

    /// <inheritdoc/>
    public Task SaveRestoreRequestAsync(RestoreRequestRow row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (this.gate)
        {
            RestoreRequestRow copy = Copy(row);
            copy.CreatedAt = this.restoreRequests.TryGetValue(row.MailboxId, out RestoreRequestRow? old) ? old.CreatedAt : DateTimeOffset.UtcNow;
            this.restoreRequests[row.MailboxId] = copy;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DeleteRestoreRequestAsync(Guid mailboxId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            this.restoreRequests.Remove(mailboxId);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<StoredMessageText>> ListStoredTextsAsync(Guid mailboxId, bool plain, int limit, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<StoredMessageText> rows = this.mailboxMessages
                .Where(m => m.MailboxId == mailboxId && (plain
                    ? !MessageCipher.IsSealed(m.Subject) || !MessageCipher.IsSealed(m.BodyText)
                    : m.Subject.StartsWith(MessageCipher.ArrivalPrefix, StringComparison.Ordinal)))
                .OrderByDescending(m => m.ReceivedAt)
                .Take(Math.Max(0, limit))
                .Select(m => new StoredMessageText(m.Id, m.FolderId, m.MaildirFile, m.Subject, m.BodyText, string.Empty))
                .ToList();
            return Task.FromResult(rows);
        }
    }

    /// <inheritdoc/>
    public Task SaveStoredTextAsync(Guid messageId, StoredText text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (this.gate)
        {
            MessageRow? row = this.mailboxMessages.Find(m => m.Id == messageId);
            if (row is not null)
            {
                row.Subject = text.Subject;
                row.BodyText = text.BodyText;
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ReplaceOutboundRawAsync(Guid id, byte[] raw, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(raw);
        lock (this.gate)
        {
            if (this.outbound.Find(o => o.Id == id) is OutboundMessage row)
            {
                row.RawBytes = raw;
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<BreakGlassRow?> GetBreakGlassAsync(CancellationToken ct = default)
    {
        lock (this.gate)
        {
            return Task.FromResult(this.breakGlass);
        }
    }

    /// <inheritdoc/>
    public Task SaveBreakGlassAsync(BreakGlassRow row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (this.gate)
        {
            this.breakGlass = row;
        }
        return Task.CompletedTask;
    }

    private static MailboxKeyRow Copy(MailboxKeyRow r) => new()
    {
        MailboxId = r.MailboxId,
        PublicKey = r.PublicKey,
        UnlockSalt = r.UnlockSalt,
        PrivateByPassword = r.PrivateByPassword,
        PrivateByOrg = r.PrivateByOrg,
        DataKey = r.DataKey,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
    };

    private static TenantKeyRow Copy(TenantKeyRow r) => new()
    {
        TenantId = r.TenantId,
        RecoveryPublic = r.RecoveryPublic,
        RecoveryBySecret = r.RecoveryBySecret,
        SecretCheck = r.SecretCheck,
        SheetConfirmedAt = r.SheetConfirmedAt,
        BreakGlassOn = r.BreakGlassOn,
        SecretByBreakGlass = r.SecretByBreakGlass,
        RecoveryByServer = r.RecoveryByServer,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
    };

    private static RestoreRequestRow Copy(RestoreRequestRow r) => new()
    {
        MailboxId = r.MailboxId,
        RestorePublic = r.RestorePublic,
        RestoreByUnlock = r.RestoreByUnlock,
        UnlockSalt = r.UnlockSalt,
        Granted = r.Granted,
        GrantedBy = r.GrantedBy,
        GrantedAt = r.GrantedAt,
        CreatedAt = r.CreatedAt,
    };
}
