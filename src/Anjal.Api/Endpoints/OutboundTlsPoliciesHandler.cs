using Anjal.Api.Dto;
using Anjal.Store;

namespace Anjal.Api.Endpoints;

/// <summary>
/// Endpoint handlers for <c>/api/outbound-tls-policies</c>.
/// </summary>
public sealed class OutboundTlsPoliciesHandler
{
    private readonly IMessageStore store;

    /// <summary>
    /// Construct with backing store.
    /// </summary>
    /// <param name="store">Store for persisting policies.</param>
    public OutboundTlsPoliciesHandler(IMessageStore store)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    /// <summary>
    /// <c>POST /api/outbound-tls-policies</c> - create or update.
    /// </summary>
    /// <param name="ctx">Request context.</param>
    public async System.Threading.Tasks.Task PostAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        string body = await ctx.ReadBodyAsync().ConfigureAwait(false);
        OutboundTlsPolicyRequest? req;
        try
        {
            req = ApiJson.Deserialize<OutboundTlsPolicyRequest>(body);
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
        if (string.IsNullOrWhiteSpace(req.Domain))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "domain is required.").ConfigureAwait(false);
            return;
        }
        if (!TryParseMode(req.Mode, out TlsMode mode))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "mode must be one of: opportunistic, required, disabled.").ConfigureAwait(false);
            return;
        }

        OutboundTlsPolicy saved = await this.store.UpsertOutboundTlsPolicyAsync(new OutboundTlsPolicy
        {
            Domain = req.Domain,
            Mode = mode,
        }).ConfigureAwait(false);

        await ctx.WriteJsonAsync(200, ToResponse(saved)).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /api/outbound-tls-policies</c> - list.
    /// </summary>
    /// <param name="ctx">Request context.</param>
    public async System.Threading.Tasks.Task ListAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.Collections.Generic.IReadOnlyList<OutboundTlsPolicy> all =
            await this.store.ListOutboundTlsPoliciesAsync().ConfigureAwait(false);

        var responses = new System.Collections.Generic.List<OutboundTlsPolicyResponse>(all.Count);
        foreach (OutboundTlsPolicy p in all)
        {
            responses.Add(ToResponse(p));
        }
        await ctx.WriteJsonAsync(200, responses).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>DELETE /api/outbound-tls-policies/{domain}</c> - remove.
    /// </summary>
    /// <param name="ctx">Request context.</param>
    /// <param name="domain">Domain from URL path.</param>
    public async System.Threading.Tasks.Task DeleteAsync(RequestContext ctx, string domain)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.ArgumentNullException.ThrowIfNull(domain);

        bool removed = await this.store.DeleteOutboundTlsPolicyAsync(domain).ConfigureAwait(false);
        if (!removed)
        {
            await ctx.WriteErrorAsync(404, "not_found", $"No policy for domain '{domain}'.").ConfigureAwait(false);
            return;
        }
        await ctx.WriteEmptyAsync(204).ConfigureAwait(false);
    }

    private static bool TryParseMode(string raw, out TlsMode mode)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "opportunistic": mode = TlsMode.Opportunistic; return true;
            case "required": mode = TlsMode.Required; return true;
            case "disabled": mode = TlsMode.Disabled; return true;
            default: mode = TlsMode.Opportunistic; return false;
        }
    }

    private static string ModeToString(TlsMode mode) => mode switch
    {
        TlsMode.Opportunistic => "opportunistic",
        TlsMode.Required => "required",
        TlsMode.Disabled => "disabled",
        _ => "opportunistic",
    };

    private static OutboundTlsPolicyResponse ToResponse(OutboundTlsPolicy p) => new()
    {
        Id = p.Id,
        Domain = p.Domain,
        Mode = ModeToString(p.Mode),
        UpdatedAt = p.UpdatedAt,
    };
}
