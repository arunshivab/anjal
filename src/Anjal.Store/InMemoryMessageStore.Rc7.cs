namespace Anjal.Store;

/// <summary>
/// v1.0.0-rc.7 additions to the in-memory store: trusted senders,
/// greylisting memory and settings. Shares the store's lock.
/// </summary>
public sealed partial class InMemoryMessageStore
{
    private readonly HashSet<(System.Guid MailboxId, string Address)> trustedSenders = new();
    private readonly List<GreylistRow> greylistRows = new();
    private readonly List<SettingRow> settingRows = new();

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> ListTrustedSendersAsync(System.Guid mailboxId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<string> list = this.trustedSenders.Where(t => t.MailboxId == mailboxId).Select(t => t.Address).OrderBy(a => a, System.StringComparer.Ordinal).ToList();
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc/>
    public Task AddTrustedSenderAsync(System.Guid mailboxId, string address, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(address);
        lock (this.gate)
        {
            this.trustedSenders.Add((mailboxId, address.Trim().ToLowerInvariant()));
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> RemoveTrustedSenderAsync(System.Guid mailboxId, string address, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(address);
        lock (this.gate)
        {
            return Task.FromResult(this.trustedSenders.Remove((mailboxId, address.Trim().ToLowerInvariant())));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<GreylistRow>> ListGreylistAsync(CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<GreylistRow> list = this.greylistRows.Select(g => new GreylistRow { Key = g.Key, FirstSeen = g.FirstSeen, LastSeen = g.LastSeen, Passed = g.Passed }).ToList();
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc/>
    public Task ReplaceGreylistAsync(IReadOnlyList<GreylistRow> rows, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(rows);
        lock (this.gate)
        {
            this.greylistRows.Clear();
            this.greylistRows.AddRange(rows.Select(g => new GreylistRow { Key = g.Key, FirstSeen = g.FirstSeen, LastSeen = g.LastSeen, Passed = g.Passed }));
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SettingRow>> ListSettingsAsync(string scope, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(scope);
        lock (this.gate)
        {
            IReadOnlyList<SettingRow> list = this.settingRows.Where(s => s.Scope == scope).OrderBy(s => s.Key, System.StringComparer.Ordinal).Select(Copy).ToList();
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc/>
    public Task<SettingRow> UpsertSettingAsync(SettingRow setting, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(setting);
        lock (this.gate)
        {
            this.settingRows.RemoveAll(s => s.Scope == setting.Scope && s.Key == setting.Key);
            SettingRow stored = Copy(setting);
            stored.UpdatedAt = System.DateTimeOffset.UtcNow;
            this.settingRows.Add(stored);
            return Task.FromResult(Copy(stored));
        }
    }

    /// <inheritdoc/>
    public Task<bool> DeleteSettingAsync(string scope, string key, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(scope);
        System.ArgumentNullException.ThrowIfNull(key);
        lock (this.gate)
        {
            return Task.FromResult(this.settingRows.RemoveAll(s => s.Scope == scope && s.Key == key) > 0);
        }
    }

    /// <inheritdoc/>
    public Task<int> ImportSettingsAsync(IReadOnlyList<SettingRow> rows, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(rows);
        int inserted = 0;
        lock (this.gate)
        {
            foreach (SettingRow row in rows)
            {
                if (!this.settingRows.Any(s => s.Scope == row.Scope && s.Key == row.Key))
                {
                    SettingRow stored = Copy(row);
                    stored.UpdatedAt = System.DateTimeOffset.UtcNow;
                    this.settingRows.Add(stored);
                    inserted++;
                }
            }
        }
        return Task.FromResult(inserted);
    }

    private static SettingRow Copy(SettingRow s) => new()
    {
        Scope = s.Scope,
        Key = s.Key,
        Value = s.Value,
        UpdatedAt = s.UpdatedAt,
        UpdatedBy = s.UpdatedBy,
    };
}
