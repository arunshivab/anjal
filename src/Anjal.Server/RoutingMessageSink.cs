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
    private readonly Anjal.Routing.IWebhookDispatcher dispatcher;
    private readonly System.Action<string>? log;

    /// <summary>
    /// Construct the sink.
    /// </summary>
    /// <param name="store">The store to persist accepted messages.</param>
    /// <param name="routing">The routing table for recipient resolution.</param>
    /// <param name="dispatcher">The webhook dispatcher to notify subscribers.</param>
    /// <param name="log">Optional log sink. Called for each accepted/rejected message.</param>
    public RoutingMessageSink(
        Anjal.Store.IMessageStore store,
        Anjal.Routing.IRoutingTable routing,
        Anjal.Routing.IWebhookDispatcher dispatcher,
        System.Action<string>? log = null)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(routing);
        System.ArgumentNullException.ThrowIfNull(dispatcher);
        this.store = store;
        this.routing = routing;
        this.dispatcher = dispatcher;
        this.log = log;
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
                this.log?.Invoke($"Reject {rcpt}: {decision.Outcome}");
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

            var payload = new Anjal.Routing.WebhookPayload
            {
                InboundMessageId = stored.Id,
                Recipient = rcpt,
                LocalPart = decision.Address.LocalPart,
                Tag = decision.Address.Tag,
                CorrelationKey = decision.Grant?.CorrelationKey ?? string.Empty,
                EnvelopeFrom = ctx.EnvelopeFrom,
                Subject = parsed.Subject,
                MessageId = parsed.MessageId,
                ReceivedAt = stored.ReceivedAt,
                RawBytesBase64 = Anjal.Mime.Base64Codec.Encode(ctx.RawBytes),
                AuthResultsJson = ctx.AuthResults is Anjal.Auth.AuthenticationResults ar
                    ? Anjal.Auth.AuthResultsJson.Serialize(ar)
                    : string.Empty,
            };

            Anjal.Routing.WebhookDispatchResult dispatch = await this.dispatcher
                .SendAsync(decision.Rule.WebhookUrl, decision.Rule.WebhookSecret, payload, ct)
                .ConfigureAwait(false);

            await this.store.SaveWebhookDeliveryAsync(
                new Anjal.Store.WebhookDelivery
                {
                    InboundMessageId = stored.Id,
                    Url = decision.Rule.WebhookUrl,
                    StatusCode = dispatch.StatusCode,
                    ErrorMessage = dispatch.ErrorMessage,
                },
                ct).ConfigureAwait(false);

            this.log?.Invoke($"Delivered {rcpt} -> {decision.Rule.WebhookUrl} (HTTP {dispatch.StatusCode})");
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
