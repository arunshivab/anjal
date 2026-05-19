namespace Anjal.Routing;

/// <summary>
/// Resolves SMTP recipient addresses to routing decisions. Backed by an
/// <see cref="Anjal.Store.IMessageStore"/> in production; can be replaced
/// with a stub in tests.
/// </summary>
public interface IRoutingTable
{
    /// <summary>
    /// Look up the routing decision for a recipient address. Pure read - this
    /// method never modifies any persisted state.
    /// </summary>
    /// <param name="recipient">A full <c>local-part[+tag]@domain</c> recipient.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The routing decision.</returns>
    System.Threading.Tasks.Task<RoutingDecision> ResolveAsync(string recipient, System.Threading.CancellationToken ct = default);
}

/// <summary>
/// Default implementation of <see cref="IRoutingTable"/> backed by a
/// store. Sub-addressing semantics: a recipient with no "+tag" matches
/// the rule for its local-part directly. A recipient with a "+tag"
/// matches the rule for its local-part only if there is an active
/// <see cref="Anjal.Store.TagGrant"/> for the (local-part, tag) pair.
/// </summary>
public sealed class StoreBackedRoutingTable : IRoutingTable
{
    private readonly Anjal.Store.IMessageStore store;
    private readonly System.Func<System.DateTimeOffset> clock;

    /// <summary>
    /// Construct the routing table.
    /// </summary>
    /// <param name="store">The backing message store.</param>
    /// <param name="clock">Optional clock for tests. Defaults to UTC now.</param>
    public StoreBackedRoutingTable(Anjal.Store.IMessageStore store, System.Func<System.DateTimeOffset>? clock = null)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        this.clock = clock ?? (() => System.DateTimeOffset.UtcNow);
    }

    /// <inheritdoc/>
    public async System.Threading.Tasks.Task<RoutingDecision> ResolveAsync(string recipient, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(recipient);

        AddressResolution? address = AddressResolution.Parse(recipient);
        if (address is null)
        {
            return new RoutingDecision
            {
                Outcome = RoutingOutcome.NoSuchMailbox,
                Address = new AddressResolution { Recipient = recipient.ToLowerInvariant() },
            };
        }

        Anjal.Store.RoutingRule? rule = await this.store.GetRoutingRuleAsync(address.LocalPart, ct).ConfigureAwait(false);
        if (rule is null)
        {
            return new RoutingDecision
            {
                Outcome = RoutingOutcome.NoSuchMailbox,
                Address = address,
            };
        }

        if (string.IsNullOrEmpty(address.Tag))
        {
            return new RoutingDecision
            {
                Outcome = RoutingOutcome.Accepted,
                Address = address,
                Rule = rule,
            };
        }

        // Recipient has a tag - require an active grant.
        System.DateTimeOffset now = this.clock();
        Anjal.Store.TagGrant? grant = await this.store
            .GetActiveTagGrantAsync(address.LocalPart, address.Tag, now, ct)
            .ConfigureAwait(false);

        if (grant is null)
        {
            return new RoutingDecision
            {
                Outcome = RoutingOutcome.TagNotAuthorised,
                Address = address,
                Rule = rule,
            };
        }

        return new RoutingDecision
        {
            Outcome = RoutingOutcome.Accepted,
            Address = address,
            Rule = rule,
            Grant = grant,
        };
    }
}
