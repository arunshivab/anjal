namespace Anjal.Server;

/// <summary>
/// Whether this server has somewhere to put mail for an address on one of its
/// own domains: a mailbox, or a routing rule that forwards it to SIGMA, Lipi
/// or another webhook. Used to refuse an unknown recipient at RCPT TO, before
/// a megabyte of attachments is transferred for a typo (DEF-042).
/// <para>
/// It only ever answers false when it is certain. A store that cannot be
/// reached, or any other doubt, returns null: the recipient is accepted and
/// the decision is made at delivery, where a temporary failure defers rather
/// than bounces (DEF-003).
/// </para>
/// </summary>
public sealed class ServerRecipientResolver : Anjal.Smtp.IRecipientResolver
{
    private readonly Anjal.Mailbox.MailboxSink mailboxes;
    private readonly Anjal.Routing.IRoutingTable? routing;

    /// <summary>Construct.</summary>
    /// <param name="mailboxes">Resolves an address to a mailbox.</param>
    /// <param name="routing">Resolves an address to a webhook rule; null when webhooks are off.</param>
    public ServerRecipientResolver(Anjal.Mailbox.MailboxSink mailboxes, Anjal.Routing.IRoutingTable? routing)
    {
        System.ArgumentNullException.ThrowIfNull(mailboxes);
        this.mailboxes = mailboxes;
        this.routing = routing;
    }

    /// <inheritdoc/>
    public async System.Threading.Tasks.Task<bool?> ExistsAsync(string address, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(address);
        if (await this.mailboxes.ResolveAsync(address, ct).ConfigureAwait(false) is not null)
        {
            return true;
        }
        if (this.routing is not null)
        {
            Anjal.Routing.RoutingDecision decision = await this.routing.ResolveAsync(address, ct).ConfigureAwait(false);
            if (decision.Outcome == Anjal.Routing.RoutingOutcome.Accepted)
            {
                return true;
            }
            // A tag that exists but is not authorised is still a real address:
            // refusing it here would tell a stranger which tags exist.
            if (decision.Outcome != Anjal.Routing.RoutingOutcome.NoSuchMailbox)
            {
                return null;
            }
        }
        return false;
    }
}
