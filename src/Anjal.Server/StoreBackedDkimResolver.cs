namespace Anjal.Server;

/// <summary>
/// Adapts <see cref="Anjal.Store.IMessageStore"/> to
/// <see cref="Anjal.Dkim.IDkimKeyResolver"/> so the outbound worker can
/// look up per-domain DKIM keys from the persistent store.
/// </summary>
public sealed class StoreBackedDkimResolver : Anjal.Dkim.IDkimKeyResolver
{
    private readonly Anjal.Store.IMessageStore store;

    /// <summary>Construct.</summary>
    /// <param name="store">The store to look up keys from.</param>
    public StoreBackedDkimResolver(Anjal.Store.IMessageStore store)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    /// <inheritdoc/>
    public async System.Threading.Tasks.Task<Anjal.Dkim.DkimKey?> ResolveAsync(string senderDomain, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(senderDomain);
        Anjal.Store.DkimKeyRow? row = await this.store.GetDkimKeyAsync(senderDomain, ct).ConfigureAwait(false);
        if (row is null) return null;
        return new Anjal.Dkim.DkimKey
        {
            Domain = row.Domain,
            Selector = row.Selector,
            PrivateKeyPem = row.PrivateKeyPem,
        };
    }
}
