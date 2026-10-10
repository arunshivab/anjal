namespace Anjal.Server;

/// <summary>
/// Adapts <see cref="Anjal.Store.IMessageStore"/> to
/// <see cref="Anjal.Dkim.IDkimKeyResolver"/> so the outbound worker can
/// look up per-domain DKIM keys from the persistent store.
/// </summary>
public sealed class StoreBackedDkimResolver : Anjal.Dkim.IDkimKeyResolver
{
    private readonly Anjal.Store.IMessageStore store;
    private readonly System.Security.Cryptography.RSA? sealKey;

    /// <summary>Construct.</summary>
    /// <param name="store">The store to look up keys from.</param>
    /// <param name="sealKey">The mail server's seal key, which opens keys the webmail locked (DES-11 S6); null when there is none.</param>
    public StoreBackedDkimResolver(Anjal.Store.IMessageStore store, System.Security.Cryptography.RSA? sealKey = null)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        this.sealKey = sealKey;
    }

    /// <inheritdoc/>
    public async System.Threading.Tasks.Task<Anjal.Dkim.DkimKey?> ResolveAsync(string senderDomain, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(senderDomain);
        Anjal.Store.DkimKeyRow? row = await this.store.GetDkimKeyAsync(senderDomain, ct).ConfigureAwait(false);
        if (row is null) return null;
        string pem = row.PrivateKeyPem;
        if (Anjal.Store.KeySeal.IsSealed(pem))
        {
            // DES-11 S6: opened here only, in memory, to sign.
            if (this.sealKey is null) return null;
            pem = Anjal.Store.KeySeal.Open(pem, this.sealKey, row.Domain, row.Selector);
        }
        return new Anjal.Dkim.DkimKey
        {
            Domain = row.Domain,
            Selector = row.Selector,
            PrivateKeyPem = pem,
        };
    }
}
