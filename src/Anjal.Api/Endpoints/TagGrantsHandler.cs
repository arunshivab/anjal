using Anjal.Api.Dto;
using Anjal.Store;

namespace Anjal.Api.Endpoints;

/// <summary>
/// Endpoint handler for <c>POST /api/tag-grants</c>.
/// </summary>
public sealed class TagGrantsHandler
{
    private readonly IMessageStore store;
    private readonly System.Func<System.DateTimeOffset> clock;

    /// <summary>
    /// Construct.
    /// </summary>
    /// <param name="store">Backing store.</param>
    /// <param name="clock">Clock for testability. Defaults to UtcNow.</param>
    public TagGrantsHandler(IMessageStore store, System.Func<System.DateTimeOffset>? clock = null)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        this.clock = clock ?? (() => System.DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// <c>POST /api/tag-grants</c> - issue a new grant.
    /// </summary>
    /// <param name="ctx">Request context.</param>
    public async System.Threading.Tasks.Task PostAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        string body = await ctx.ReadBodyAsync().ConfigureAwait(false);
        TagGrantRequest? req;
        try
        {
            req = ApiJson.Deserialize<TagGrantRequest>(body);
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
        if (string.IsNullOrWhiteSpace(req.LocalPart))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "localPart is required.").ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(req.Tag))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "tag is required.").ConfigureAwait(false);
            return;
        }
        if (req.TtlSeconds <= 0)
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "ttlSeconds must be positive.").ConfigureAwait(false);
            return;
        }

        System.DateTimeOffset now = this.clock();
        TagGrant saved = await this.store.CreateTagGrantAsync(new TagGrant
        {
            LocalPart = req.LocalPart,
            Tag = req.Tag,
            CorrelationKey = req.CorrelationKey ?? string.Empty,
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(req.TtlSeconds),
        }).ConfigureAwait(false);

        await ctx.WriteJsonAsync(200, new TagGrantResponse
        {
            Id = saved.Id,
            LocalPart = saved.LocalPart,
            Tag = saved.Tag,
            CorrelationKey = saved.CorrelationKey,
            CreatedAt = saved.CreatedAt,
            ExpiresAt = saved.ExpiresAt,
        }).ConfigureAwait(false);
    }
}
