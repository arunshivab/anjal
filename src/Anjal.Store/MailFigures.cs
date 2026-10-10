namespace Anjal.Store;

/// <summary>
/// What a dashboard counts for one period (rc.15, items 56 to 61): totals
/// only - never which messages, never who. A scope is one mailbox, one
/// organisation, or the whole service.
/// </summary>
public sealed class MailFigures
{
    /// <summary>The highest spam score with its own bar; higher scores share the last ("10+").</summary>
    public const int TopScore = 10;

    /// <summary>Messages received and kept out of Junk.</summary>
    public long Received { get; set; }

    /// <summary>Messages sent (copies in Sent).</summary>
    public long Sent { get; set; }

    /// <summary>Messages filed in Junk.</summary>
    public long Junk { get; set; }

    /// <summary>Messages that went through the incoming checks and have a score.</summary>
    public long Checked { get; set; }

    /// <summary>
    /// Received (Junk left out, as in <see cref="Received"/>) and sent, step by step through the period:
    /// each step's start in the asked zone's local time. Steps with nothing
    /// are left out.
    /// </summary>
    public IReadOnlyList<TimeBucket> Series { get; set; } = System.Array.Empty<TimeBucket>();

    /// <summary>Checked messages by spam score, 0 to <see cref="TopScore"/> (the last is that score and above).</summary>
    public long[] Scores { get; set; } = new long[TopScore + 1];
}

/// <summary>One step of a dashboard's time chart (rc.15).</summary>
/// <param name="Start">The step's start, in the viewer's local time.</param>
/// <param name="Received">Received in it (Junk left out).</param>
/// <param name="Sent">Sent in it.</param>
public sealed record TimeBucket(System.DateTime Start, long Received, long Sent);

/// <summary>How a time chart steps (rc.15, item 56).</summary>
public enum TimeStep
{
    /// <summary>By hour (Today, Yesterday).</summary>
    Hour = 0,

    /// <summary>By day (last 7 and 30 days).</summary>
    Day = 1,

    /// <summary>By week, from Monday (last 3 months).</summary>
    Week = 2,

    /// <summary>By month (last 12 months).</summary>
    Month = 3,
}

/// <summary>Who a dashboard's figures cover (rc.15).</summary>
/// <param name="MailboxId">One mailbox, or null.</param>
/// <param name="TenantId">One organisation, or null; both null is the whole service.</param>
public sealed record FigureScope(System.Guid? MailboxId, System.Guid? TenantId)
{
    /// <summary>The whole service.</summary>
    public static FigureScope Service { get; } = new(null, null);

    /// <summary>One mailbox.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <returns>The scope.</returns>
    public static FigureScope Mailbox(System.Guid mailboxId) => new(mailboxId, null);

    /// <summary>One organisation.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <returns>The scope.</returns>
    public static FigureScope Organisation(System.Guid tenantId) => new(null, tenantId);
}

/// <summary>An address's outbound mail in a period (rc.15, the applications box).</summary>
/// <param name="Sender">The envelope sender.</param>
/// <param name="Sent">Accepted by the receiving server.</param>
/// <param name="Waiting">Still waiting or retrying.</param>
/// <param name="Failed">Given up on (bounced).</param>
public sealed record SenderTraffic(string Sender, long Sent, long Waiting, long Failed);

/// <summary>Outbound mail from one sending domain in a period (DES-11 D8: sudden rises in sent and bounced mail).</summary>
/// <param name="Domain">The envelope sender's domain, lower case.</param>
/// <param name="Sent">Accepted by the receiving server.</param>
/// <param name="Waiting">Still waiting or retrying.</param>
/// <param name="Failed">Given up on (bounced).</param>
public sealed record SenderDomainTraffic(string Domain, long Sent, long Waiting, long Failed)
{
    /// <summary>Everything queued from the domain in the period.</summary>
    public long Queued => this.Sent + this.Waiting + this.Failed;
}

/// <summary>Outbound mail to one receiving domain in a period (rc.15, item 61).</summary>
/// <param name="Domain">The receiving domain, lower case.</param>
/// <param name="Sent">Accepted by the receiving server.</param>
/// <param name="Waiting">Still waiting or retrying.</param>
/// <param name="Failed">Given up on (bounced).</param>
public sealed record RecipientDomainTraffic(string Domain, long Sent, long Waiting, long Failed);
