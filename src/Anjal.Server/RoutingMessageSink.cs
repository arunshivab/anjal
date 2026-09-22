namespace Anjal.Server;

/// <summary>
/// Production <see cref="Anjal.Smtp.IMessageSink"/> implementation. For each
/// accepted SMTP message: parses the MIME, persists it, resolves the recipient,
/// and fires a signed webhook.
/// </summary>
public sealed class RoutingMessageSink : Anjal.Smtp.IMessageSink
{
    private readonly Anjal.Store.IMessageStore store;
    private readonly Anjal.Routing.IRoutingTable routing;
    private readonly System.Action<string>? log;
    private readonly System.Action? onQueued;

    /// <summary>
    /// Construct the sink.
    /// </summary>
    /// <param name="store">The store to persist accepted messages.</param>
    /// <param name="routing">The routing table for recipient resolution.</param>
    /// <param name="dispatcher">The webhook dispatcher to notify subscribers.</param>
    /// <param name="log">Optional log sink. Called for each accepted/rejected message.</param>
    /// <param name="onQueued">Called after each notification is queued, to wake the webhook worker.</param>
    public RoutingMessageSink(
        Anjal.Store.IMessageStore store,
        Anjal.Routing.IRoutingTable routing,
        Anjal.Routing.IWebhookDispatcher dispatcher,
        System.Action<string>? log = null,
        System.Action? onQueued = null)
    {
        // The dispatcher is kept in the signature for callers; delivery itself
        // now happens in WebhookWorker, which owns its own dispatcher.
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(routing);
        System.ArgumentNullException.ThrowIfNull(dispatcher);
        this.store = store;
        this.routing = routing;
        this.log = log;
        this.onQueued = onQueued;
    }

    /// <inheritdoc/>
    public async System.Threading.Tasks.Task<Anjal.Smtp.DeliveryResult> DeliverAsync(
        Anjal.Smtp.DeliveryContext ctx,
        System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.EnvelopeTo.Count == 0)
        {
            return new Anjal.Smtp.DeliveryResult
            {
                Outcome = Anjal.Smtp.DeliveryOutcome.PermanentFailure,
                ReplyText = "No recipients",
            };
        }

        // For v1 each RCPT gets its own row. We resolve each independently.
        // If any recipient is unroutable we reject the whole DATA - the SMTP
        // client would have been told 250 at RCPT time so this should be rare.
        Anjal.Mime.MimeMessage parsed;
        try
        {
            parsed = Anjal.Mime.MimeParser.Parse(ctx.RawBytes);
        }
#pragma warning disable CA1031 // Intentional: any MIME error should not crash the server.
        catch (System.Exception ex)
        {
            this.log?.Invoke($"MIME parse failed: {ex.GetType().Name}: {ex.Message}");
            return new Anjal.Smtp.DeliveryResult
            {
                Outcome = Anjal.Smtp.DeliveryOutcome.PermanentFailure,
                ReplyText = "Message could not be parsed",
            };
        }
#pragma warning restore CA1031

        int delivered = 0;
        foreach (string rcpt in ctx.EnvelopeTo)
        {
            Anjal.Routing.RoutingDecision decision = await this.routing.ResolveAsync(rcpt, ct).ConfigureAwait(false);
            if (decision.Outcome != Anjal.Routing.RoutingOutcome.Accepted || decision.Rule is null)
            {
                // Most recipients have no webhook rule: they are mailboxes, and
                // the mailbox stage delivers them. Saying "Reject" here, next to
                // a successful delivery, sent readers down the wrong path
                // (DEF-025). Only a refusal that means something is logged.
                if (decision.Outcome != Anjal.Routing.RoutingOutcome.NoSuchMailbox)
                {
                    this.log?.Invoke($"Webhook routing: {rcpt} not routed ({decision.Outcome})");
                }
                continue;
            }

            var stored = new Anjal.Store.InboundMessage
            {
                EnvelopeFrom = ctx.EnvelopeFrom,
                EnvelopeTo = rcpt,
                LocalPart = decision.Address.LocalPart,
                Tag = decision.Address.Tag,
                Subject = parsed.Subject,
                MessageId = parsed.MessageId,
                RawBytes = ctx.RawBytes,
            };
            stored = await this.store.SaveInboundMessageAsync(stored, ct).ConfigureAwait(false);

            // Queue the notification durably and return. The SMTP client is
            // answered as soon as the message and its job are stored; the
            // worker delivers it (with retries) independently of this
            // transaction, so a slow or failing receiver never holds a
            // connection and never loses a message.
            System.DateTimeOffset now = System.DateTimeOffset.UtcNow;
            await this.store.EnqueueWebhookJobAsync(new Anjal.Store.WebhookJob
            {
                InboundMessageId = stored.Id,
                Recipient = rcpt,
                LocalPart = decision.Address.LocalPart,
                Tag = decision.Address.Tag,
                CorrelationKey = decision.Grant?.CorrelationKey ?? string.Empty,
                AuthResultsJson = ctx.AuthResults is Anjal.Auth.AuthenticationResults ar
                    ? Anjal.Auth.AuthResultsJson.Serialize(ar)
                    : string.Empty,
                NextAttemptAt = now,
                GiveUpAt = now + WebhookWorker.GiveUpAfter,
            }, ct).ConfigureAwait(false);
            this.onQueued?.Invoke();

            this.log?.Invoke($"Accepted {rcpt}; webhook to {decision.Rule.WebhookUrl} queued");
            delivered++;
        }

        if (delivered == 0)
        {
            return new Anjal.Smtp.DeliveryResult
            {
                Outcome = Anjal.Smtp.DeliveryOutcome.PermanentFailure,
                ReplyText = "No accepted recipients",
            };
        }

        return new Anjal.Smtp.DeliveryResult
        {
            Outcome = Anjal.Smtp.DeliveryOutcome.Accepted,
            ReplyText = $"Accepted for {delivered} recipient(s)",
        };
    }
}
