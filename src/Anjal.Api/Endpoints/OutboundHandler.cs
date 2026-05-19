using System.Text;
using Anjal.Api.Dto;
using Anjal.Store;

namespace Anjal.Api.Endpoints;

/// <summary>
/// Endpoint handlers for <c>/api/outbound</c>.
/// </summary>
public sealed class OutboundHandler
{
    private readonly IMessageStore store;
    private readonly System.Func<System.DateTimeOffset> clock;

    /// <summary>
    /// Construct with backing store.
    /// </summary>
    /// <param name="store">Store for the outbound queue.</param>
    /// <param name="clock">Clock for testability.</param>
    public OutboundHandler(IMessageStore store, System.Func<System.DateTimeOffset>? clock = null)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        this.clock = clock ?? (() => System.DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// <c>POST /api/outbound</c> - enqueue an outbound message.
    /// </summary>
    /// <param name="ctx">Request context.</param>
    public async System.Threading.Tasks.Task PostAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        string body = await ctx.ReadBodyAsync().ConfigureAwait(false);
        OutboundRequest? req;
        try
        {
            req = ApiJson.Deserialize<OutboundRequest>(body);
        }
        catch (System.Text.Json.JsonException ex)
        {
            await ctx.WriteErrorAsync(400, "invalid_json", ex.Message).ConfigureAwait(false);
            return;
        }
        if (req is null)
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "Body is empty.").ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(req.EnvelopeFrom))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "envelopeFrom is required.").ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(req.EnvelopeTo))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "envelopeTo is required.").ConfigureAwait(false);
            return;
        }

        byte[] rawBytes;
        if (!string.IsNullOrEmpty(req.RawBytesBase64))
        {
            try
            {
                rawBytes = System.Convert.FromBase64String(req.RawBytesBase64);
            }
            catch (System.FormatException ex)
            {
                await ctx.WriteErrorAsync(400, "invalid_request", $"rawBytesBase64 is not valid base64: {ex.Message}").ConfigureAwait(false);
                return;
            }
        }
        else
        {
            // Build a simple text/plain message from the structured fields.
            if (string.IsNullOrEmpty(req.Subject) && string.IsNullOrEmpty(req.BodyText))
            {
                await ctx.WriteErrorAsync(400, "invalid_request", "Provide either rawBytesBase64 or subject+bodyText.").ConfigureAwait(false);
                return;
            }
            rawBytes = BuildSimpleMessage(req);
        }

        System.DateTimeOffset now = this.clock();
        System.DateTimeOffset giveUp = req.GiveUpHours > 0
            ? now.AddHours(req.GiveUpHours)
            : now.AddHours(24);

        OutboundMessage saved = await this.store.EnqueueOutboundAsync(new OutboundMessage
        {
            EnvelopeFrom = req.EnvelopeFrom,
            EnvelopeTo = req.EnvelopeTo,
            RawBytes = rawBytes,
            CreatedAt = now,
            NextAttemptAt = now,
            GiveUpAt = giveUp,
        }).ConfigureAwait(false);

        await ctx.WriteJsonAsync(202, ToResponse(saved)).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /api/outbound/{id}</c> - fetch status of a queued message.
    /// </summary>
    /// <param name="ctx">Request context.</param>
    /// <param name="idString">The outbound message identifier as a string.</param>
    public async System.Threading.Tasks.Task GetAsync(RequestContext ctx, string idString)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.ArgumentNullException.ThrowIfNull(idString);

        if (!System.Guid.TryParse(idString, out System.Guid id))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "Outbound id is not a valid UUID.").ConfigureAwait(false);
            return;
        }

        OutboundMessage? found = await this.store.GetOutboundByIdAsync(id).ConfigureAwait(false);
        if (found is null)
        {
            await ctx.WriteErrorAsync(404, "not_found", $"No outbound message with id {id}.").ConfigureAwait(false);
            return;
        }
        await ctx.WriteJsonAsync(200, ToResponse(found)).ConfigureAwait(false);
    }

    private static byte[] BuildSimpleMessage(OutboundRequest req)
    {
        // Hand-build a minimal text/plain RFC 5322 message. Headers default
        // to envelope addresses if explicit From/To not provided.
        string from = string.IsNullOrEmpty(req.FromHeader) ? req.EnvelopeFrom : req.FromHeader;
        string to = string.IsNullOrEmpty(req.ToHeader) ? req.EnvelopeTo : req.ToHeader;

        var sb = new StringBuilder(512);
        sb.Append("From: ").Append(from).Append("\r\n");
        sb.Append("To: ").Append(to).Append("\r\n");
        if (!string.IsNullOrEmpty(req.Subject))
        {
            sb.Append("Subject: ").Append(req.Subject).Append("\r\n");
        }
        sb.Append("MIME-Version: 1.0\r\n");
        sb.Append("Content-Type: text/plain; charset=utf-8\r\n");
        sb.Append("Date: ");
        sb.Append(System.DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", System.Globalization.CultureInfo.InvariantCulture));
        sb.Append("\r\n");
        sb.Append("\r\n");
        sb.Append(req.BodyText ?? string.Empty);
        if (!sb.ToString().EndsWith("\r\n", System.StringComparison.Ordinal))
        {
            sb.Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static OutboundResponse ToResponse(OutboundMessage m) => new()
    {
        Id = m.Id,
        EnvelopeFrom = m.EnvelopeFrom,
        EnvelopeTo = m.EnvelopeTo,
        Status = m.Status.ToString(),
        Attempts = m.Attempts,
        CreatedAt = m.CreatedAt,
        NextAttemptAt = m.NextAttemptAt,
        GiveUpAt = m.GiveUpAt,
        LastError = m.LastError,
    };
}
