namespace Anjal.Routing;

/// <summary>
/// Possible outcomes when routing an inbound recipient address.
/// </summary>
public enum RoutingOutcome
{
    /// <summary>A matching routing rule was found and the message should be delivered.</summary>
    Accepted = 0,

    /// <summary>No routing rule matches the local-part. Reject with SMTP 550.</summary>
    NoSuchMailbox = 1,

    /// <summary>The address has a tag, but the tag is not authorised by an active grant. Reject with SMTP 550.</summary>
    TagNotAuthorised = 2,

    /// <summary>The address has a tag whose grant has expired. Reject with SMTP 550.</summary>
    TagExpired = 3,
}

/// <summary>
/// The result of asking <see cref="IRoutingTable"/> to route a recipient.
/// Encapsulates both success (matched rule + optional grant) and failure
/// (an outcome explaining why the address won't be accepted).
/// </summary>
public sealed class RoutingDecision
{
    /// <summary>The outcome of the routing attempt.</summary>
    public RoutingOutcome Outcome { get; init; }

    /// <summary>The resolved address components used for the lookup.</summary>
    public AddressResolution Address { get; init; } = new();

    /// <summary>The matched routing rule, present only when <see cref="Outcome"/> is <see cref="RoutingOutcome.Accepted"/>.</summary>
    public Anjal.Store.RoutingRule? Rule { get; init; }

    /// <summary>The active tag grant when the recipient carried a tag, otherwise <see langword="null"/>.</summary>
    public Anjal.Store.TagGrant? Grant { get; init; }

    /// <summary>The SMTP reply text appropriate for the outcome, suitable for inclusion in a 550 response.</summary>
    public string SmtpReplyText => this.Outcome switch
    {
        RoutingOutcome.Accepted => "OK",
        RoutingOutcome.NoSuchMailbox => "No such user here",
        RoutingOutcome.TagNotAuthorised => "Address tag not authorised",
        RoutingOutcome.TagExpired => "Address tag expired",
        _ => "Mailbox unavailable",
    };
}
