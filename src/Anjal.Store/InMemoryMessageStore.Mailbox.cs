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
    private readonly List<SenderRuleRow> senderRules = new();

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
                existing.SpamThreshold = tenant.SpamThreshold;
                return Task.FromResult(Clone(existing));
            }
            var row = new TenantRow
            {
                Id = System.Guid.NewGuid(),
                Slug = slug,
                DisplayName = tenant.DisplayName,
                Enabled = tenant.Enabled,
                SpamThreshold = tenant.SpamThreshold,
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
            this.senderRules.RemoveAll(r => r.TenantId == found.Id);
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
                existing.Theme = string.IsNullOrWhiteSpace(mailbox.Theme) ? existing.Theme : mailbox.Theme;
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
                Theme = string.IsNullOrWhiteSpace(mailbox.Theme) ? MailboxRow.DefaultTheme : mailbox.Theme,
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

    /// <inheritdoc/>
    public Task<long> CountUnreadAsync(System.Guid mailboxId, System.Guid? folderId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            long count = 0;
            foreach (MessageRow m in this.mailboxMessages)
            {
                if (m.MailboxId == mailboxId && !m.Seen && (folderId is null || m.FolderId == folderId.Value))
                {
                    count++;
                }
            }
            return Task.FromResult(count);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<MessageRow>> SearchMessagesAsync(System.Guid mailboxId, System.Guid? folderId, string query, int limit, int offset, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(query);
        lock (this.gate)
        {
            List<MessageRow> matched = this.SearchLocked(mailboxId, folderId, query);
            var page = new List<MessageRow>();
            for (int i = System.Math.Max(0, offset); i < matched.Count && page.Count < System.Math.Max(0, limit); i++)
            {
                page.Add(Clone(matched[i]));
            }
            return Task.FromResult<IReadOnlyList<MessageRow>>(page);
        }
    }

    /// <inheritdoc/>
    public Task<long> CountSearchAsync(System.Guid mailboxId, System.Guid? folderId, string query, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(query);
        lock (this.gate)
        {
            return Task.FromResult((long)this.SearchLocked(mailboxId, folderId, query).Count);
        }
    }

    private List<MessageRow> SearchLocked(System.Guid mailboxId, System.Guid? folderId, string query)
    {
        string q = query.Trim();
        var matched = new List<MessageRow>();
        foreach (MessageRow m in this.mailboxMessages)
        {
            if (m.MailboxId != mailboxId || (folderId is not null && m.FolderId != folderId.Value))
            {
                continue;
            }
            if (q.Length == 0 ||
                m.Subject.Contains(q, System.StringComparison.OrdinalIgnoreCase) ||
                m.FromHeader.Contains(q, System.StringComparison.OrdinalIgnoreCase) ||
                m.ToHeader.Contains(q, System.StringComparison.OrdinalIgnoreCase) ||
                m.EnvelopeFrom.Contains(q, System.StringComparison.OrdinalIgnoreCase))
            {
                matched.Add(m);
            }
        }
        matched.Sort((a, b) =>
        {
            int c = b.ReceivedAt.CompareTo(a.ReceivedAt);
            return c != 0 ? c : this.mailboxMessages.IndexOf(b).CompareTo(this.mailboxMessages.IndexOf(a));
        });
        return matched;
    }

    /// <inheritdoc/>
    public Task<MessageRow?> SetMessageFlagsAsync(System.Guid id, bool seen, bool flagged, bool answered, string? maildirFile, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            MessageRow? found = this.mailboxMessages.Find(m => m.Id == id);
            if (found is null)
            {
                return Task.FromResult<MessageRow?>(null);
            }
            found.Seen = seen;
            found.Flagged = flagged;
            found.Answered = answered;
            if (maildirFile is not null)
            {
                found.MaildirFile = maildirFile;
            }
            return Task.FromResult<MessageRow?>(Clone(found));
        }
    }

    /// <inheritdoc/>
    public Task<MessageRow?> MoveMessageAsync(System.Guid id, System.Guid folderId, string maildirFile, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(maildirFile);
        lock (this.gate)
        {
            MessageRow? found = this.mailboxMessages.Find(m => m.Id == id);
            if (found is null)
            {
                return Task.FromResult<MessageRow?>(null);
            }
            found.FolderId = folderId;
            found.MaildirFile = maildirFile;
            return Task.FromResult<MessageRow?>(Clone(found));
        }
    }

    /// <inheritdoc/>
    public Task<bool> DeleteMessageAsync(System.Guid id, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            return Task.FromResult(this.mailboxMessages.RemoveAll(m => m.Id == id) > 0);
        }
    }

    /// <inheritdoc/>
    public Task<SenderRuleRow> UpsertSenderRuleAsync(SenderRuleRow rule, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(rule);
        lock (this.gate)
        {
            string pattern = rule.Pattern.Trim().ToLowerInvariant();
            SenderRuleRow? existing = this.senderRules.Find(r =>
                r.TenantId == rule.TenantId && string.Equals(r.Pattern, pattern, System.StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Action = rule.Action;
                return Task.FromResult(Clone(existing));
            }
            var row = new SenderRuleRow
            {
                Id = System.Guid.NewGuid(),
                TenantId = rule.TenantId,
                Pattern = pattern,
                Action = rule.Action,
                CreatedAt = System.DateTimeOffset.UtcNow,
            };
            this.senderRules.Add(row);
            return Task.FromResult(Clone(row));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SenderRuleRow>> ListSenderRulesAsync(System.Guid tenantId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            var snapshot = new List<SenderRuleRow>();
            foreach (SenderRuleRow r in this.senderRules)
            {
                if (r.TenantId == tenantId)
                {
                    snapshot.Add(Clone(r));
                }
            }
            snapshot.Sort((a, b) => string.CompareOrdinal(a.Pattern, b.Pattern));
            return Task.FromResult<IReadOnlyList<SenderRuleRow>>(snapshot);
        }
    }

    /// <inheritdoc/>
    public Task<bool> DeleteSenderRuleAsync(System.Guid tenantId, string pattern, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(pattern);
        lock (this.gate)
        {
            int removed = this.senderRules.RemoveAll(r =>
                r.TenantId == tenantId && string.Equals(r.Pattern, pattern.Trim(), System.StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(removed > 0);
        }
    }

    private static SenderRuleRow Clone(SenderRuleRow r) => new()
    {
        Id = r.Id,
        TenantId = r.TenantId,
        Pattern = r.Pattern,
        Action = r.Action,
        CreatedAt = r.CreatedAt,
    };

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
        SpamThreshold = t.SpamThreshold,
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
        Theme = m.Theme,
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
        SpamScore = m.SpamScore,
        ReceivedAt = m.ReceivedAt,
        CategoryId = m.CategoryId,
        HasAttachments = m.HasAttachments,
    };

    // ================= Categories (v0.15.0) =================

    private readonly List<CategoryRow> categories = new();
    private readonly List<CategoryRuleRow> categoryRules = new();

    /// <inheritdoc/>
    public Task<CategoryRow> UpsertCategoryAsync(CategoryRow category, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(category);
        lock (this.gate)
        {
            CategoryRow? existing = category.Id != System.Guid.Empty
                ? this.categories.Find(c => c.Id == category.Id)
                : this.categories.Find(c => c.TenantId == category.TenantId && c.MailboxId == category.MailboxId &&
                                            string.Equals(c.Name, category.Name, System.StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Name = category.Name;
                existing.Slot = category.Slot;
                return Task.FromResult(Clone(existing));
            }
            var row = new CategoryRow
            {
                Id = System.Guid.NewGuid(),
                TenantId = category.TenantId,
                MailboxId = category.MailboxId,
                Name = category.Name,
                Slot = category.Slot,
                CreatedAt = System.DateTimeOffset.UtcNow,
            };
            this.categories.Add(row);
            return Task.FromResult(Clone(row));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<CategoryRow>> ListCategoriesAsync(System.Guid tenantId, System.Guid? mailboxId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            var result = new List<CategoryRow>();
            foreach (CategoryRow c in this.categories)
            {
                if (c.TenantId != tenantId)
                {
                    continue;
                }
                if (c.MailboxId is null || (mailboxId is not null && c.MailboxId == mailboxId))
                {
                    result.Add(Clone(c));
                }
            }
            result.Sort((a, b) =>
            {
                int shared = (a.IsShared ? 0 : 1).CompareTo(b.IsShared ? 0 : 1);
                if (shared != 0)
                {
                    return shared;
                }
                int slot = a.Slot.CompareTo(b.Slot);
                return slot != 0 ? slot : string.CompareOrdinal(a.Name, b.Name);
            });
            return Task.FromResult<IReadOnlyList<CategoryRow>>(result);
        }
    }

    /// <inheritdoc/>
    public Task<CategoryRow?> GetCategoryAsync(System.Guid id, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            CategoryRow? row = this.categories.Find(c => c.Id == id);
            return Task.FromResult(row is null ? null : Clone(row));
        }
    }

    /// <inheritdoc/>
    public Task<bool> DeleteCategoryAsync(System.Guid id, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            int removed = this.categories.RemoveAll(c => c.Id == id);
            this.categoryRules.RemoveAll(r => r.CategoryId == id);
            foreach (MessageRow m in this.mailboxMessages)
            {
                if (m.CategoryId == id)
                {
                    m.CategoryId = null;
                }
            }
            return Task.FromResult(removed > 0);
        }
    }

    /// <inheritdoc/>
    public Task<MessageRow?> SetMessageCategoryAsync(System.Guid mailboxId, System.Guid messageId, System.Guid? categoryId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            MessageRow? row = this.mailboxMessages.Find(m => m.Id == messageId && m.MailboxId == mailboxId);
            if (row is null)
            {
                return Task.FromResult<MessageRow?>(null);
            }
            row.CategoryId = categoryId;
            return Task.FromResult<MessageRow?>(Clone(row));
        }
    }

    /// <inheritdoc/>
    public Task<CategoryRuleRow> UpsertCategoryRuleAsync(CategoryRuleRow rule, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(rule);
        lock (this.gate)
        {
            CategoryRuleRow? existing = this.categoryRules.Find(r => r.MailboxId == rule.MailboxId &&
                string.Equals(r.Pattern, rule.Pattern, System.StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.CategoryId = rule.CategoryId;
                return Task.FromResult(new CategoryRuleRow
                {
                    Id = existing.Id,
                    MailboxId = existing.MailboxId,
                    Pattern = existing.Pattern,
                    CategoryId = existing.CategoryId,
                    CreatedAt = existing.CreatedAt,
                });
            }
            var row = new CategoryRuleRow
            {
                Id = System.Guid.NewGuid(),
                MailboxId = rule.MailboxId,
                Pattern = rule.Pattern.ToLowerInvariant(),
                CategoryId = rule.CategoryId,
                CreatedAt = System.DateTimeOffset.UtcNow,
            };
            this.categoryRules.Add(row);
            return Task.FromResult(row);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<CategoryRuleRow>> ListCategoryRulesAsync(System.Guid mailboxId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            var result = new List<CategoryRuleRow>();
            foreach (CategoryRuleRow r in this.categoryRules)
            {
                if (r.MailboxId == mailboxId)
                {
                    result.Add(new CategoryRuleRow { Id = r.Id, MailboxId = r.MailboxId, Pattern = r.Pattern, CategoryId = r.CategoryId, CreatedAt = r.CreatedAt });
                }
            }
            return Task.FromResult<IReadOnlyList<CategoryRuleRow>>(result);
        }
    }

    /// <inheritdoc/>
    public Task<bool> DeleteCategoryRuleAsync(System.Guid id, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            return Task.FromResult(this.categoryRules.RemoveAll(r => r.Id == id) > 0);
        }
    }

    /// <inheritdoc/>
    public Task<MailboxActivity> GetActivityAsync(System.Guid mailboxId, System.DateTimeOffset periodStart, System.DateTimeOffset periodEnd, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            var folders = new Dictionary<System.Guid, string>();
            foreach (FolderRow f in this.folders)
            {
                if (f.MailboxId == mailboxId)
                {
                    folders[f.Id] = f.Name;
                }
            }
            var names = new Dictionary<System.Guid, (string Name, int Slot)>();
            foreach (CategoryRow c in this.categories)
            {
                names[c.Id] = (c.Name, c.Slot);
            }

            var activity = new MailboxActivity { From = periodStart, To = periodEnd };
            var byFolder = new Dictionary<string, long>(System.StringComparer.Ordinal);
            var byCategory = new Dictionary<string, (long Count, int Slot)>(System.StringComparer.Ordinal);
            var bySender = new Dictionary<string, long>(System.StringComparer.OrdinalIgnoreCase);
            var byDay = new Dictionary<System.DateTimeOffset, (long Received, long Sent)>();

            foreach (MessageRow m in this.mailboxMessages)
            {
                if (m.MailboxId != mailboxId || m.ReceivedAt < periodStart || m.ReceivedAt >= periodEnd)
                {
                    continue;
                }
                string folder = folders.TryGetValue(m.FolderId, out string? f) ? f : "Unknown";
                bool sent = string.Equals(folder, "Sent", System.StringComparison.Ordinal);
                bool junk = string.Equals(folder, "Junk", System.StringComparison.Ordinal);
                bool draft = string.Equals(folder, "Drafts", System.StringComparison.Ordinal);
                System.DateTimeOffset day = new(m.ReceivedAt.UtcDateTime.Date, System.TimeSpan.Zero);
                byDay.TryGetValue(day, out (long Received, long Sent) counts);

                if (sent)
                {
                    activity.Sent++;
                    if (m.HasAttachments)
                    {
                        activity.SentWithAttachments++;
                    }
                    byDay[day] = (counts.Received, counts.Sent + 1);
                    continue;
                }
                if (draft)
                {
                    continue;
                }

                byDay[day] = (counts.Received + 1, counts.Sent);
                if (junk)
                {
                    activity.Junked++;
                }
                else
                {
                    activity.Delivered++;
                }
                if (m.HasAttachments)
                {
                    activity.ReceivedWithAttachments++;
                }

                byFolder.TryGetValue(folder, out long fc);
                byFolder[folder] = fc + 1;

                string categoryName = m.CategoryId is System.Guid cid && names.TryGetValue(cid, out (string Name, int Slot) cat) ? cat.Name : "Uncategorised";
                int slot = m.CategoryId is System.Guid cid2 && names.TryGetValue(cid2, out (string Name, int Slot) cat2) ? cat2.Slot : CategoryRow.NoSlot;
                byCategory.TryGetValue(categoryName, out (long Count, int Slot) cc);
                byCategory[categoryName] = (cc.Count + 1, slot);

                string sender = SenderKey(m);
                bySender.TryGetValue(sender, out long sc);
                bySender[sender] = sc + 1;
            }

            activity.ByFolder = Rank(byFolder);
            var cats = new List<NamedCount>();
            foreach (KeyValuePair<string, (long Count, int Slot)> kv in byCategory)
            {
                cats.Add(new NamedCount { Name = kv.Key, Count = kv.Value.Count, Slot = kv.Value.Slot });
            }
            cats.Sort((a, b) => b.Count.CompareTo(a.Count));
            activity.ByCategory = cats;
            activity.TopSenders = Rank(bySender, 5);

            var days = new List<DailyCount>();
            foreach (KeyValuePair<System.DateTimeOffset, (long Received, long Sent)> kv in byDay)
            {
                days.Add(new DailyCount { Day = kv.Key, Received = kv.Value.Received, Sent = kv.Value.Sent });
            }
            days.Sort((a, b) => a.Day.CompareTo(b.Day));
            activity.ByDay = days;

            activity.RecoveredFromJunk = this.recoveredFromJunk.TryGetValue(mailboxId, out long r) ? r : 0;
            return Task.FromResult(activity);
        }
    }

    private readonly Dictionary<System.Guid, long> recoveredFromJunk = new();

    /// <summary>Record that a message was moved out of Junk by the reader.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    public void NoteRecoveredFromJunk(System.Guid mailboxId)
    {
        lock (this.gate)
        {
            this.recoveredFromJunk.TryGetValue(mailboxId, out long n);
            this.recoveredFromJunk[mailboxId] = n + 1;
        }
    }

    private static string SenderKey(MessageRow m)
    {
        string from = m.FromHeader.Length > 0 ? m.FromHeader : m.EnvelopeFrom;
        int lt = from.LastIndexOf('<');
        int gt = from.LastIndexOf('>');
        if (lt >= 0 && gt > lt)
        {
            return from.Substring(lt + 1, gt - lt - 1).Trim();
        }
        return from.Trim();
    }

    private static List<NamedCount> Rank(Dictionary<string, long> source, int? take = null)
    {
        var list = new List<NamedCount>();
        foreach (KeyValuePair<string, long> kv in source)
        {
            list.Add(new NamedCount { Name = kv.Key, Count = kv.Value });
        }
        list.Sort((a, b) =>
        {
            int c = b.Count.CompareTo(a.Count);
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        });
        return take is int n && list.Count > n ? list.GetRange(0, n) : list;
    }

    private static CategoryRow Clone(CategoryRow c) => new()
    {
        Id = c.Id,
        TenantId = c.TenantId,
        MailboxId = c.MailboxId,
        Name = c.Name,
        Slot = c.Slot,
        CreatedAt = c.CreatedAt,
    };

}
