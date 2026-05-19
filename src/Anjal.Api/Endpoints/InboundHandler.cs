using Anjal.Api.Dto;
using Anjal.Store;

namespace Anjal.Api.Endpoints;

/// <summary>
/// Endpoint handler for <c>GET /api/inbound/{id}</c>.
/// </summary>
public sealed class InboundHandler
{
    private readonly IMessageStore store;

    /// <summary>
    /// Construct.
    /// </summary>
    /// <param name="store">Backing store.</param>
    public InboundHandler(IMessageStore store)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    /// <summary>
    /// <c>GET /api/inbound/{id}</c> - fetch a stored inbound message.
    /// </summary>
    /// <param name="ctx">Request context.</param>
    /// <param name="idString">Identifier from the URL path.</param>
    public async System.Threading.Tasks.Task GetAsync(RequestContext ctx, string idString)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.ArgumentNullException.ThrowIfNull(idString);

        if (!System.Guid.TryParse(idString, out System.Guid id))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "Inbound id is not a valid UUID.").ConfigureAwait(false);
            return;
        }

        InboundMessage? found = await this.store.GetInboundByIdAsync(id).ConfigureAwait(false);
        if (found is null)
        {
            await ctx.WriteErrorAsync(404, "not_found", $"No inbound message with id {id}.").ConfigureAwait(false);
            return;
        }

        await ctx.WriteJsonAsync(200, new InboundResponse
        {
            Id = found.Id,
            EnvelopeFrom = found.EnvelopeFrom,
            EnvelopeTo = found.EnvelopeTo,
            LocalPart = found.LocalPart,
            Tag = found.Tag,
            Subject = found.Subject,
            MessageId = found.MessageId,
            ReceivedAt = found.ReceivedAt,
            RawBytesBase64 = System.Convert.ToBase64String(found.RawBytes),
        }).ConfigureAwait(false);
    }
}
