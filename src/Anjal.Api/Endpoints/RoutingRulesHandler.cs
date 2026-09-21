using Anjal.Api.Dto;
using Anjal.Store;

namespace Anjal.Api.Endpoints;

/// <summary>
/// Endpoint handlers for <c>/api/routing-rules</c>.
/// </summary>
public sealed class RoutingRulesHandler
{
    private readonly IMessageStore store;
    private readonly Anjal.Routing.WebhookTargetPolicy webhookPolicy;

    /// <summary>
    /// Construct with the backing store.
    /// </summary>
    /// <param name="store">Store for persisting rules.</param>
    public RoutingRulesHandler(IMessageStore store)
        : this(store, Anjal.Routing.WebhookTargetPolicy.FromEnvironment())
    {
    }

    /// <summary>Construct with an explicit webhook target policy.</summary>
    /// <param name="store">The store.</param>
    /// <param name="webhookPolicy">Where webhooks may point.</param>
    public RoutingRulesHandler(IMessageStore store, Anjal.Routing.WebhookTargetPolicy webhookPolicy)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        System.ArgumentNullException.ThrowIfNull(webhookPolicy);
        this.webhookPolicy = webhookPolicy;
    }

    /// <summary>
    /// <c>POST /api/routing-rules</c> - create or update a rule.
    /// </summary>
    /// <param name="ctx">Request context.</param>
    public async System.Threading.Tasks.Task PostAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        string body = await ctx.ReadBodyAsync().ConfigureAwait(false);
        RoutingRuleRequest? req;
        try
        {
            req = ApiJson.Deserialize<RoutingRuleRequest>(body);
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
        if (string.IsNullOrWhiteSpace(req.WebhookUrl))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "webhookUrl is required.").ConfigureAwait(false);
            return;
        }
        string? urlProblem = this.webhookPolicy.Validate(req.WebhookUrl.Trim());
        if (urlProblem is not null)
        {
            await ctx.WriteErrorAsync(400, "invalid_request", urlProblem).ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(req.WebhookSecret))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "webhookSecret is required.").ConfigureAwait(false);
            return;
        }

        RoutingRule saved = await this.store.UpsertRoutingRuleAsync(new RoutingRule
        {
            LocalPart = req.LocalPart,
            WebhookUrl = req.WebhookUrl.Trim(),
            WebhookSecret = req.WebhookSecret,
        }).ConfigureAwait(false);

        await ctx.WriteJsonAsync(200, new RoutingRuleResponse
        {
            Id = saved.Id,
            LocalPart = saved.LocalPart,
            WebhookUrl = saved.WebhookUrl,
            CreatedAt = saved.CreatedAt,
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /api/routing-rules</c> - list all rules.
    /// </summary>
    /// <param name="ctx">Request context.</param>
    public async System.Threading.Tasks.Task ListAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.Collections.Generic.IReadOnlyList<RoutingRule> all =
            await this.store.ListRoutingRulesAsync().ConfigureAwait(false);

        var responses = new System.Collections.Generic.List<RoutingRuleResponse>(all.Count);
        foreach (RoutingRule r in all)
        {
            responses.Add(new RoutingRuleResponse
            {
                Id = r.Id,
                LocalPart = r.LocalPart,
                WebhookUrl = r.WebhookUrl,
                CreatedAt = r.CreatedAt,
            });
        }

        await ctx.WriteJsonAsync(200, responses).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>DELETE /api/routing-rules/{localPart}</c> - remove a rule.
    /// </summary>
    /// <param name="ctx">Request context.</param>
    /// <param name="localPart">The local-part to remove.</param>
    public async System.Threading.Tasks.Task DeleteAsync(RequestContext ctx, string localPart)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.ArgumentNullException.ThrowIfNull(localPart);

        bool removed = await this.store.DeleteRoutingRuleAsync(localPart).ConfigureAwait(false);
        if (!removed)
        {
            await ctx.WriteErrorAsync(404, "not_found", $"No rule for local-part '{localPart}'.").ConfigureAwait(false);
            return;
        }
        await ctx.WriteEmptyAsync(204).ConfigureAwait(false);
    }
}
