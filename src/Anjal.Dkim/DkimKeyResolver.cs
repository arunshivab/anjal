namespace Anjal.Dkim;

/// <summary>
/// Resolves a <see cref="DkimKey"/> for a sender domain. The
/// <c>OutboundWorker</c> calls this once per outbound message, parsing the
/// From header to extract the domain. If no key is found, the worker
/// hard-fails the send (the "refuse to send unsigned" policy).
/// </summary>
public interface IDkimKeyResolver
{
    /// <summary>
    /// Look up the signing key for a sender domain. Returns null if no
    /// key is configured for that domain.
    /// </summary>
    /// <param name="senderDomain">The lowercase domain from the From header.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<DkimKey?> ResolveAsync(string senderDomain, System.Threading.CancellationToken ct = default);
}

/// <summary>
/// Resolves a single key configured via constructor. Used when only one
/// sender domain is in play (the env-var-driven configuration).
/// </summary>
public sealed class SingleKeyResolver : IDkimKeyResolver
{
    private readonly DkimKey key;

    /// <summary>Construct.</summary>
    /// <param name="key">The single key.</param>
    public SingleKeyResolver(DkimKey key)
    {
        System.ArgumentNullException.ThrowIfNull(key);
        this.key = key;
    }

    /// <inheritdoc/>
    public System.Threading.Tasks.Task<DkimKey?> ResolveAsync(string senderDomain, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(senderDomain);
        if (string.Equals(senderDomain, this.key.Domain, System.StringComparison.OrdinalIgnoreCase))
        {
            return System.Threading.Tasks.Task.FromResult<DkimKey?>(this.key);
        }
        return System.Threading.Tasks.Task.FromResult<DkimKey?>(null);
    }
}

/// <summary>
/// Tries each resolver in order; returns the first non-null result.
/// </summary>
public sealed class ChainedKeyResolver : IDkimKeyResolver
{
    private readonly System.Collections.Generic.IReadOnlyList<IDkimKeyResolver> resolvers;

    /// <summary>Construct from an ordered list of resolvers.</summary>
    /// <param name="resolvers">Resolvers, in priority order.</param>
    public ChainedKeyResolver(params IDkimKeyResolver[] resolvers)
    {
        System.ArgumentNullException.ThrowIfNull(resolvers);
        this.resolvers = resolvers;
    }

    /// <inheritdoc/>
    public async System.Threading.Tasks.Task<DkimKey?> ResolveAsync(string senderDomain, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(senderDomain);
        foreach (IDkimKeyResolver r in this.resolvers)
        {
            DkimKey? hit = await r.ResolveAsync(senderDomain, ct).ConfigureAwait(false);
            if (hit is not null) return hit;
        }
        return null;
    }
}
