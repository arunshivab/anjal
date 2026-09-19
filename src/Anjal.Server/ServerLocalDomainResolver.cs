namespace Anjal.Server;

/// <summary>
/// Local-domain resolver that chains three sources: an env-var list, the
/// persistent <see cref="Anjal.Store.IMessageStore"/>-backed
/// <c>local_domains</c> table, and the
/// <see cref="Anjal.Store.IMailboxStore"/>-backed <c>tenant_domains</c>
/// table. A domain is local if it appears in any source, so registering
/// a tenant domain makes it RCPT-able immediately.
/// </summary>
public sealed class ServerLocalDomainResolver : Anjal.Smtp.ILocalDomainResolver
{
    private readonly System.Collections.Generic.HashSet<string> envDomains;
    private readonly Anjal.Store.IMessageStore? store;
    private readonly Anjal.Store.IMailboxStore? mailboxStore;

    /// <summary>
    /// Construct with optional env-var list and optional store.
    /// </summary>
    /// <param name="envDomains">Domains from env (already lowercased).</param>
    /// <param name="store">Optional DB-backed source.</param>
    public ServerLocalDomainResolver(
        System.Collections.Generic.IEnumerable<string> envDomains,
        Anjal.Store.IMessageStore? store)
        : this(envDomains, store, mailboxStore: null)
    {
    }

    /// <summary>
    /// Construct with optional env-var list, optional store and optional
    /// mailbox store.
    /// </summary>
    /// <param name="envDomains">Domains from env (already lowercased).</param>
    /// <param name="store">Optional DB-backed source (<c>local_domains</c>).</param>
    /// <param name="mailboxStore">Optional mailbox registry (<c>tenant_domains</c>).</param>
    public ServerLocalDomainResolver(
        System.Collections.Generic.IEnumerable<string> envDomains,
        Anjal.Store.IMessageStore? store,
        Anjal.Store.IMailboxStore? mailboxStore)
    {
        System.ArgumentNullException.ThrowIfNull(envDomains);
        this.envDomains = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (string d in envDomains)
        {
            if (!string.IsNullOrWhiteSpace(d))
            {
                this.envDomains.Add(d.Trim().ToLowerInvariant());
            }
        }
        this.store = store;
        this.mailboxStore = mailboxStore;
    }

    /// <inheritdoc/>
    public async System.Threading.Tasks.Task<bool> IsLocalAsync(string domain, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(domain);
        if (string.IsNullOrWhiteSpace(domain))
        {
            return false;
        }

        if (this.envDomains.Contains(domain))
        {
            return true;
        }

        if (this.store is not null && await this.store.IsLocalDomainAsync(domain, ct).ConfigureAwait(false))
        {
            return true;
        }

        if (this.mailboxStore is not null)
        {
            Anjal.Store.TenantDomainRow? row = await this.mailboxStore.GetTenantDomainAsync(domain, ct).ConfigureAwait(false);
            return row is not null;
        }

        return false;
    }
}
