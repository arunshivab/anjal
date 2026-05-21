namespace Anjal.Server;

/// <summary>
/// Local-domain resolver that chains two sources: an env-var list and
/// the persistent <see cref="Anjal.Store.IMessageStore"/>-backed
/// <c>local_domains</c> table. A domain is local if it appears in
/// either source.
/// </summary>
public sealed class ServerLocalDomainResolver : Anjal.Smtp.ILocalDomainResolver
{
    private readonly System.Collections.Generic.HashSet<string> envDomains;
    private readonly Anjal.Store.IMessageStore? store;

    /// <summary>
    /// Construct with optional env-var list and optional store.
    /// </summary>
    /// <param name="envDomains">Domains from env (already lowercased).</param>
    /// <param name="store">Optional DB-backed source.</param>
    public ServerLocalDomainResolver(
        System.Collections.Generic.IEnumerable<string> envDomains,
        Anjal.Store.IMessageStore? store)
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

        if (this.store is null)
        {
            return false;
        }

        return await this.store.IsLocalDomainAsync(domain, ct).ConfigureAwait(false);
    }
}
