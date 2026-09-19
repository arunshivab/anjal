namespace Anjal.Store;

/// <summary>
/// <see cref="IMailboxStore"/> half of the in-memory store. Shares the
/// same lock as the <see cref="IMessageStore"/> half.
/// </summary>
public sealed partial class InMemoryMessageStore
{
    private readonly List<TenantRow> tenants = new();
    private readonly List<TenantDomainRow> tenantDomains = new();
    private readonly List<MailboxRow> mailboxes = new();
    private readonly List<FolderRow> folders = new();
    private readonly List<MessageRow> mailboxMessages = new();

    /// <summary>The mailbox message rows currently stored. Provided for inspection in tests.</summary>
    public IReadOnlyList<MessageRow> MailboxMessages
    {
        get
        {
            lock (this.gate)
            {
                return this.mailboxMessages.ToArray();
            }
        }
    }

    /// <inheritdoc/>
    public Task<TenantRow> UpsertTenantAsync(TenantRow tenant, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(tenant);
        lock (this.gate)
        {
            string slug = tenant.Slug.Trim().ToLowerInvariant();
            TenantRow? existing = this.tenants.Find(t =>
                string.Equals(t.Slug, slug, System.StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.DisplayName = tenant.DisplayName;
                existing.Enabled = tenant.Enabled;
                return Task.FromResult(Clone(existing));
            }
            var row = new TenantRow
            {
                Id = System.Guid.NewGuid(),
                Slug = slug,
                DisplayName = tenant.DisplayName,
                Enabled = tenant.Enabled,
                CreatedAt = System.DateTimeOffset.UtcNow,
            };
            this.tenants.Add(row);
            return Task.FromResult(Clone(row));
        }
    }

    /// <inheritdoc/>
    public Task<TenantRow?> GetTenantAsync(string slug, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(slug);
        lock (this.gate)
        {
            TenantRow? found = this.tenants.Find(t =>
                string.Equals(t.Slug, slug, System.StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(found is null ? null : Clone(found));
        }
    }

    /// <inheritdoc/>
    public Task<TenantRow?> GetTenantByIdAsync(System.Guid id, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            TenantRow? found = this.tenants.Find(t => t.Id == id);
            return Task.FromResult(found is null ? null : Clone(found));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<TenantRow>> ListTenantsAsync(CancellationToken ct = default)
    {
        lock (this.gate)
        {
            var snapshot = new List<TenantRow>(this.tenants.Count);
            foreach (TenantRow t in this.tenants)
            {
                snapshot.Add(Clone(t));
            }
            snapshot.Sort((a, b) => string.CompareOrdinal(a.Slug, b.Slug));
            return Task.FromResult<IReadOnlyList<TenantRow>>(snapshot);
        }
    }

    /// <inheritdoc/>
    public Task<bool> DeleteTenantAsync(string slug, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(slug);
        lock (this.gate)
        {
            TenantRow? found = this.tenants.Find(t =>
                string.Equals(t.Slug, slug, System.StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                return Task.FromResult(false);
            }
            foreach (MailboxRow mb in this.mailboxes.FindAll(m => m.TenantId == found.Id))
            {
                this.RemoveMailboxCascade(mb.Id);
            }
            this.mailboxes.RemoveAll(m => m.TenantId == found.Id);
            this.tenantDomains.RemoveAll(d => d.TenantId == found.Id);
            this.tenants.Remove(found);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc/>
    public Task<TenantDomainRow> UpsertTenantDomainAsync(TenantDomainRow domain, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(domain);
        lock (this.gate)
        {
            string normalized = domain.Domain.Trim().ToLowerInvariant();
            TenantDomainRow? existing = this.tenantDomains.Find(d =>
                string.Equals(d.Domain, normalized, System.StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.TenantId = domain.TenantId;
                existing.Verified = domain.Verified;
                return Task.FromResult(Clone(existing));
            }
            var row = new TenantDomainRow
            {
                Id = System.Guid.NewGuid(),
                TenantId = domain.TenantId,
                Domain = normalized,
                Verified = domain.Verified,
                CreatedAt = System.DateTimeOffset.UtcNow,
            };
            this.tenantDomains.Add(row);
            return Task.FromResult(Clone(row));
        }
    }

    /// <inheritdoc/>
    public Task<TenantDomainRow?> GetTenantDomainAsync(string domain, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(domain);
        lock (this.gate)
        {
            TenantDomainRow? found = this.tenantDomains.Find(d =>
                string.Equals(d.Domain, domain, System.StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(found is null ? null : Clone(found));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<TenantDomainRow>> ListTenantDomainsAsync(System.Guid? tenantId = null, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            var snapshot = new List<TenantDomainRow>();
            foreach (TenantDomainRow d in this.tenantDomains)
            {
                if (tenantId is null || d.TenantId == tenantId.Value)
                {
                    snapshot.Add(Clone(d));
                }
            }
            snapshot.Sort((a, b) => string.CompareOrdinal(a.Domain, b.Domain));
            return Task.FromResult<IReadOnlyList<TenantDomainRow>>(snapshot);
        }
    }

    /// <inheritdoc/>
    public Task<bool> DeleteTenantDomainAsync(string domain, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(domain);
        lock (this.gate)
        {
            int removed = this.tenantDomains.RemoveAll(d =>
                string.Equals(d.Domain, domain, System.StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(removed > 0);
        }
    }

    /// <inheritdoc/>
    public Task<MailboxRow> UpsertMailboxAsync(MailboxRow mailbox, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(mailbox);
        lock (this.gate)
        {
            string local = mailbox.LocalPart.Trim().ToLowerInvariant();
            string domain = mailbox.Domain.Trim().ToLowerInvariant();
            MailboxRow? existing = this.mailboxes.Find(m =>
                string.Equals(m.LocalPart, local, System.StringComparison.OrdinalIgnoreCase) &&
                string.Equals(m.Domain, domain, System.StringComparison.OrdinalIgnoreCase));
            System.DateTimeOffset now = System.DateTimeOffset.UtcNow;
            if (existing is not null)
            {
                existing.TenantId = mailbox.TenantId;
                existing.DisplayName = mailbox.DisplayName;
                existing.Enabled = mailbox.Enabled;
                existing.QuotaBytes = mailbox.QuotaBytes;
                if (!string.IsNullOrEmpty(mailbox.PasswordPbkdf2))
                {
                    existing.PasswordPbkdf2 = mailbox.PasswordPbkdf2;
                }
                existing.UpdatedAt = now;
                return Task.FromResult(Clone(existing));
            }
            var row = new MailboxRow
            {
                Id = System.Guid.NewGuid(),
                TenantId = mailbox.TenantId,
                LocalPart = local,
                Domain = domain,
                PasswordPbkdf2 = mailbox.PasswordPbkdf2,
                DisplayName = mailbox.DisplayName,
                Enabled = mailbox.Enabled,
                QuotaBytes = mailbox.QuotaBytes,
                UsedBytes = 0,
                CreatedAt = now,
                UpdatedAt = now,
            };
            this.mailboxes.Add(row);
            return Task.FromResult(Clone(row));
        }
    }

    /// <inheritdoc/>
    public Task<MailboxRow?> GetMailboxAsync(string localPart, string domain, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(localPart);
        System.ArgumentNullException.ThrowIfNull(domain);
        lock (this.gate)
        {
            MailboxRow? found = this.mailboxes.Find(m =>
                string.Equals(m.LocalPart, localPart, System.StringComparison.OrdinalIgnoreCase) &&
                string.Equals(m.Domain, domain, System.StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(found is null ? null : Clone(found));
        }
    }

    /// <inheritdoc/>
    public Task<MailboxRow?> GetMailboxByIdAsync(System.Guid id, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            MailboxRow? found = this.mailboxes.Find(m => m.Id == id);
            return Task.FromResult(found is null ? null : Clone(found));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<MailboxRow>> ListMailboxesAsync(System.Guid? tenantId = null, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            var snapshot = new List<MailboxRow>();
            foreach (MailboxRow m in this.mailboxes)
            {
                if (tenantId is null || m.TenantId == tenantId.Value)
                {
                    snapshot.Add(Clone(m));
                }
            }
            snapshot.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.Domain, b.Domain);
                return c != 0 ? c : string.CompareOrdinal(a.LocalPart, b.LocalPart);
            });
            return Task.FromResult<IReadOnlyList<MailboxRow>>(snapshot);
        }
    }

    /// <inheritdoc/>
    public Task<bool> DeleteMailboxAsync(string localPart, string domain, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(localPart);
        System.ArgumentNullException.ThrowIfNull(domain);
        lock (this.gate)
        {
            MailboxRow? found = this.mailboxes.Find(m =>
                string.Equals(m.LocalPart, localPart, System.StringComparison.OrdinalIgnoreCase) &&
                string.Equals(m.Domain, domain, System.StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                return Task.FromResult(false);
            }
            this.RemoveMailboxCascade(found.Id);
            this.mailboxes.Remove(found);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc/>
    public Task<long?> AddMailboxUsageAsync(System.Guid mailboxId, long deltaBytes, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            MailboxRow? found = this.mailboxes.Find(m => m.Id == mailboxId);
            if (found is null)
            {
                return Task.FromResult<long?>(null);
            }
            found.UsedBytes += deltaBytes;
            if (found.UsedBytes < 0)
            {
                found.UsedBytes = 0;
            }
            return Task.FromResult<long?>(found.UsedBytes);
        }
    }

    /// <inheritdoc/>
    public Task<FolderRow> EnsureFolderAsync(System.Guid mailboxId, string name, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(name);
        lock (this.gate)
        {
            FolderRow? existing = this.folders.Find(f =>
                f.MailboxId == mailboxId && string.Equals(f.Name, name, System.StringComparison.Ordinal));
            if (existing is not null)
            {
                return Task.FromResult(Clone(existing));
            }
            var row = new FolderRow
            {
                Id = System.Guid.NewGuid(),
                MailboxId = mailboxId,
                Name = name,
                CreatedAt = System.DateTimeOffset.UtcNow,
            };
            this.folders.Add(row);
            return Task.FromResult(Clone(row));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<FolderRow>> ListFoldersAsync(System.Guid mailboxId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            var snapshot = new List<FolderRow>();
            foreach (FolderRow f in this.folders)
            {
                if (f.MailboxId == mailboxId)
                {
                    snapshot.Add(Clone(f));
                }
            }
            snapshot.Sort(CompareFolders);
            return Task.FromResult<IReadOnlyList<FolderRow>>(snapshot);
        }
    }

    /// <inheritdoc/>
    public Task<MessageRow> SaveMessageAsync(MessageRow message, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(message);
        lock (this.gate)
        {
            var row = Clone(message);
            row.Id = System.Guid.NewGuid();
            row.ReceivedAt = System.DateTimeOffset.UtcNow;
            this.mailboxMessages.Add(row);
            return Task.FromResult(Clone(row));
        }
    }

    /// <inheritdoc/>
    public Task<MessageRow?> GetMessageByIdAsync(System.Guid id, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            MessageRow? found = this.mailboxMessages.Find(m => m.Id == id);
            return Task.FromResult(found is null ? null : Clone(found));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<MessageRow>> ListMessagesAsync(System.Guid mailboxId, System.Guid? folderId, int limit, int offset, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            var matched = new List<MessageRow>();
            foreach (MessageRow m in this.mailboxMessages)
            {
                if (m.MailboxId == mailboxId && (folderId is null || m.FolderId == folderId.Value))
                {
                    matched.Add(m);
                }
            }
            // Newest first; ties broken by insertion order (stable via index).
            matched.Sort((a, b) =>
            {
                int c = b.ReceivedAt.CompareTo(a.ReceivedAt);
                return c != 0 ? c : this.mailboxMessages.IndexOf(b).CompareTo(this.mailboxMessages.IndexOf(a));
            });
            var page = new List<MessageRow>();
            for (int i = System.Math.Max(0, offset); i < matched.Count && page.Count < System.Math.Max(0, limit); i++)
            {
                page.Add(Clone(matched[i]));
            }
            return Task.FromResult<IReadOnlyList<MessageRow>>(page);
        }
    }

    /// <inheritdoc/>
    public Task<long> CountMessagesAsync(System.Guid mailboxId, System.Guid? folderId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            long count = 0;
            foreach (MessageRow m in this.mailboxMessages)
            {
                if (m.MailboxId == mailboxId && (folderId is null || m.FolderId == folderId.Value))
                {
                    count++;
                }
            }
            return Task.FromResult(count);
        }
    }

    private void RemoveMailboxCascade(System.Guid mailboxId)
    {
        this.mailboxMessages.RemoveAll(m => m.MailboxId == mailboxId);
        this.folders.RemoveAll(f => f.MailboxId == mailboxId);
    }

    private static int CompareFolders(FolderRow a, FolderRow b)
    {
        bool aInbox = string.Equals(a.Name, FolderRow.Inbox, System.StringComparison.Ordinal);
        bool bInbox = string.Equals(b.Name, FolderRow.Inbox, System.StringComparison.Ordinal);
        if (aInbox != bInbox)
        {
            return aInbox ? -1 : 1;
        }
        return string.CompareOrdinal(a.Name, b.Name);
    }

    private static TenantRow Clone(TenantRow t) => new()
    {
        Id = t.Id,
        Slug = t.Slug,
        DisplayName = t.DisplayName,
        Enabled = t.Enabled,
        CreatedAt = t.CreatedAt,
    };

    private static TenantDomainRow Clone(TenantDomainRow d) => new()
    {
        Id = d.Id,
        TenantId = d.TenantId,
        Domain = d.Domain,
        Verified = d.Verified,
        CreatedAt = d.CreatedAt,
    };

    private static MailboxRow Clone(MailboxRow m) => new()
    {
        Id = m.Id,
        TenantId = m.TenantId,
        LocalPart = m.LocalPart,
        Domain = m.Domain,
        PasswordPbkdf2 = m.PasswordPbkdf2,
        DisplayName = m.DisplayName,
        Enabled = m.Enabled,
        QuotaBytes = m.QuotaBytes,
        UsedBytes = m.UsedBytes,
        CreatedAt = m.CreatedAt,
        UpdatedAt = m.UpdatedAt,
    };

    private static FolderRow Clone(FolderRow f) => new()
    {
        Id = f.Id,
        MailboxId = f.MailboxId,
        Name = f.Name,
        CreatedAt = f.CreatedAt,
    };

    private static MessageRow Clone(MessageRow m) => new()
    {
        Id = m.Id,
        MailboxId = m.MailboxId,
        FolderId = m.FolderId,
        MaildirFile = m.MaildirFile,
        EnvelopeFrom = m.EnvelopeFrom,
        MessageId = m.MessageId,
        FromHeader = m.FromHeader,
        ToHeader = m.ToHeader,
        Subject = m.Subject,
        DateHeader = m.DateHeader,
        SizeBytes = m.SizeBytes,
        Seen = m.Seen,
        Flagged = m.Flagged,
        Answered = m.Answered,
        ReceivedAt = m.ReceivedAt,
    };
}
