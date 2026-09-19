namespace Anjal.Smtp;

/// <summary>
/// Fans one delivery out to several <see cref="IMessageSink"/>s and merges
/// their results. Every sink sees every message; each decides for itself
/// which recipients it owns. The merged outcome is
/// <see cref="DeliveryOutcome.Accepted"/> if any sink accepted,
/// otherwise <see cref="DeliveryOutcome.TransientFailure"/> if any sink
/// reported a transient failure, otherwise
/// <see cref="DeliveryOutcome.PermanentFailure"/>. This lets a mailbox
/// sink and a webhook-routing sink coexist: an address can be a mailbox,
/// a webhook target, or both.
/// </summary>
public sealed class CompositeMessageSink : IMessageSink
{
    private readonly IReadOnlyList<IMessageSink> sinks;

    /// <summary>
    /// Construct with the sinks to fan out to, in call order.
    /// </summary>
    /// <param name="sinks">One or more sinks.</param>
    public CompositeMessageSink(params IMessageSink[] sinks)
    {
        System.ArgumentNullException.ThrowIfNull(sinks);
        if (sinks.Length == 0)
        {
            throw new System.ArgumentException("At least one sink is required.", nameof(sinks));
        }
        foreach (IMessageSink s in sinks)
        {
            System.ArgumentNullException.ThrowIfNull(s, nameof(sinks));
        }
        this.sinks = sinks;
    }

    /// <summary>The sinks this composite fans out to, in call order.</summary>
    public IReadOnlyList<IMessageSink> Sinks => this.sinks;

    /// <inheritdoc/>
    public async Task<DeliveryResult> DeliverAsync(DeliveryContext ctx, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);

        var accepted = new List<string>();
        var transient = new List<string>();
        var permanent = new List<string>();

        foreach (IMessageSink sink in this.sinks)
        {
            DeliveryResult r = await sink.DeliverAsync(ctx, ct).ConfigureAwait(false);
            switch (r.Outcome)
            {
                case DeliveryOutcome.Accepted:
                    accepted.Add(r.ReplyText);
                    break;
                case DeliveryOutcome.TransientFailure:
                    transient.Add(r.ReplyText);
                    break;
                default:
                    permanent.Add(r.ReplyText);
                    break;
            }
        }

        if (accepted.Count > 0)
        {
            return new DeliveryResult
            {
                Outcome = DeliveryOutcome.Accepted,
                ReplyText = string.Join("; ", accepted),
            };
        }
        if (transient.Count > 0)
        {
            return new DeliveryResult
            {
                Outcome = DeliveryOutcome.TransientFailure,
                ReplyText = string.Join("; ", transient),
            };
        }
        return new DeliveryResult
        {
            Outcome = DeliveryOutcome.PermanentFailure,
            ReplyText = permanent.Count > 0 ? string.Join("; ", permanent) : "No accepted recipients",
        };
    }
}
