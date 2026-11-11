namespace Anjal.Store;

/// <summary>rc.28 additions to the in-memory store: app passwords, IMAP numbers and import jobs (DES-20).</summary>
public sealed partial class InMemoryMessageStore : IMailAppStore
{
    private readonly List<AppPasswordRow> appPasswords = new();
    private readonly Dictionary<System.Guid, (long Validity, long Next)> imapFolders = new();
    private readonly Dictionary<System.Guid, (System.Guid Folder, long Uid)> imapUids = new();
    private readonly List<ImportJobRow> importJobs = new();

    /// <inheritdoc/>
    public Task<AppPasswordRow> AddAppPasswordAsync(AppPasswordRow row, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(row);
        lock (this.gate)
        {
            AppPasswordRow copy = CloneApp(row);
            if (copy.Id == System.Guid.Empty)
            {
                copy.Id = System.Guid.NewGuid();
            }
            this.appPasswords.Add(copy);
            return Task.FromResult(CloneApp(copy));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<AppPasswordRow>> ListAppPasswordsAsync(System.Guid mailboxId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<AppPasswordRow> list = this.appPasswords.Where(a => a.MailboxId == mailboxId && a.RemovedAt is null)
                .OrderByDescending(a => a.CreatedAt).Select(CloneApp).ToList();
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc/>
    public Task<bool> RemoveAppPasswordAsync(System.Guid mailboxId, System.Guid id, System.DateTimeOffset at, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            AppPasswordRow? a = this.appPasswords.FirstOrDefault(x => x.Id == id && x.MailboxId == mailboxId && x.RemovedAt is null);
            if (a is null)
            {
                return Task.FromResult(false);
            }
            a.RemovedAt = at;
            a.PrivateByApp = null;
            a.Hash = string.Empty;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc/>
    public Task TouchAppPasswordAsync(System.Guid id, System.DateTimeOffset at, string from, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(from);
        lock (this.gate)
        {
            if (this.appPasswords.FirstOrDefault(x => x.Id == id) is AppPasswordRow a)
            {
                a.LastUsedAt = at;
                a.LastUsedFrom = from;
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<ImapFolderNumbers> AssignImapUidsAsync(System.Guid folderId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            if (!this.imapFolders.TryGetValue(folderId, out (long Validity, long Next) f))
            {
                f = (System.DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1);
            }
            long next = f.Next;
            foreach (MessageRow m in this.mailboxMessages.Where(m => m.FolderId == folderId).OrderBy(m => m.ReceivedAt).ThenBy(m => m.Id))
            {
                if (!this.imapUids.TryGetValue(m.Id, out (System.Guid Folder, long Uid) u) || u.Folder != folderId)
                {
                    this.imapUids[m.Id] = (folderId, next++);
                }
            }
            this.imapFolders[folderId] = (f.Validity, next);
            IReadOnlyList<(System.Guid, long)> uids = this.mailboxMessages.Where(m => m.FolderId == folderId)
                .Select(m => (m.Id, this.imapUids[m.Id].Uid)).OrderBy(x => x.Item2).ToList();
            return Task.FromResult(new ImapFolderNumbers(f.Validity, next, uids));
        }
    }

    /// <inheritdoc/>
    public Task SetReceivedAtAsync(System.Guid messageId, System.DateTimeOffset at, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            if (this.mailboxMessages.FirstOrDefault(m => m.Id == messageId) is MessageRow row)
            {
                row.ReceivedAt = at;
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<ImportJobRow> AddImportJobAsync(ImportJobRow job, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(job);
        lock (this.gate)
        {
            ImportJobRow copy = CloneJob(job);
            if (copy.Id == System.Guid.Empty)
            {
                copy.Id = System.Guid.NewGuid();
            }
            this.importJobs.Add(copy);
            return Task.FromResult(CloneJob(copy));
        }
    }

    /// <inheritdoc/>
    public Task<ImportJobRow?> GetImportJobAsync(System.Guid id, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            ImportJobRow? j = this.importJobs.FirstOrDefault(x => x.Id == id);
            return Task.FromResult(j is null ? null : CloneJob(j));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ImportJobRow>> ListImportJobsAsync(System.Guid mailboxId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<ImportJobRow> list = this.importJobs.Where(j => j.MailboxId == mailboxId).OrderByDescending(j => j.CreatedAt).Select(CloneJob).ToList();
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ImportJobRow>> ListOpenImportJobsAsync(CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<ImportJobRow> list = this.importJobs.Where(j => j.State is ImportJobRow.Waiting or ImportJobRow.Running).OrderBy(j => j.CreatedAt).Select(CloneJob).ToList();
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc/>
    public Task<int> ExpireChoosingImportJobsAsync(System.DateTimeOffset before, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            int n = 0;
            foreach (ImportJobRow j in this.importJobs.Where(j => j.State == "choosing" && j.CreatedAt < before))
            {
                j.State = ImportJobRow.Stopped;
                j.Secret = string.Empty;
                j.UpdatedAt = System.DateTimeOffset.UtcNow;
                n++;
            }
            return Task.FromResult(n);
        }
    }

    /// <inheritdoc/>
    public Task<bool> ClaimImportJobAsync(System.Guid id, System.DateTimeOffset staleBefore, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            ImportJobRow? j = this.importJobs.FirstOrDefault(x => x.Id == id);
            if (j is null || !(j.State == ImportJobRow.Waiting || (j.State == ImportJobRow.Running && j.UpdatedAt < staleBefore)))
            {
                return Task.FromResult(false);
            }
            j.State = ImportJobRow.Running;
            j.UpdatedAt = System.DateTimeOffset.UtcNow;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc/>
    public Task SaveImportJobAsync(ImportJobRow job, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(job);
        lock (this.gate)
        {
            int i = this.importJobs.FindIndex(x => x.Id == job.Id);
            if (i >= 0 && (this.importJobs[i].State != ImportJobRow.Stopped || job.State == ImportJobRow.Stopped))
            {
                ImportJobRow saved = CloneJob(job);
                saved.UpdatedAt = System.DateTimeOffset.UtcNow;
                this.importJobs[i] = saved;
            }
        }
        return Task.CompletedTask;
    }

    private static AppPasswordRow CloneApp(AppPasswordRow a) => new()
    {
        Id = a.Id,
        MailboxId = a.MailboxId,
        Name = a.Name,
        Hash = a.Hash,
        UnlockSalt = a.UnlockSalt,
        PrivateByApp = a.PrivateByApp,
        CreatedAt = a.CreatedAt,
        LastUsedAt = a.LastUsedAt,
        LastUsedFrom = a.LastUsedFrom,
        RemovedAt = a.RemovedAt,
    };

    private static ImportJobRow CloneJob(ImportJobRow j) => new()
    {
        Id = j.Id,
        MailboxId = j.MailboxId,
        Kind = j.Kind,
        Source = j.Source,
        Secret = j.Secret,
        TargetFolder = j.TargetFolder,
        Folders = j.Folders,
        Cursor = j.Cursor,
        State = j.State,
        Seen = j.Seen,
        Imported = j.Imported,
        Skipped = j.Skipped,
        Total = j.Total,
        Error = j.Error,
        CreatedAt = j.CreatedAt,
        UpdatedAt = j.UpdatedAt,
    };
}
