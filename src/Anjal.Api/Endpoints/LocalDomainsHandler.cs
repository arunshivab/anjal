using Anjal.Api.Dto;
using Anjal.Store;

namespace Anjal.Api.Endpoints;

/// <summary>
/// Endpoint handlers for <c>/api/local-domains</c>. The MTA port uses
/// this list to refuse RCPT TO for non-local destinations (open-relay
/// guard).
/// </summary>
public sealed class LocalDomainsHandler
{
    private readonly IMessageStore store;

    /// <summary>Construct.</summary>
    /// <param name="store">Backing store.</param>
    public LocalDomainsHandler(IMessageStore store)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    /// <summary><c>POST /api/local-domains</c> - register a domain.</summary>
    /// <param name="ctx">Request context.</param>
    public async System.Threading.Tasks.Task PostAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        string body = await ctx.ReadBodyAsync().ConfigureAwait(false);
        LocalDomainRequest? req;
        try
        {
            req = ApiJson.Deserialize<LocalDomainRequest>(body);
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

        LocalDomainRow saved = await this.store.UpsertLocalDomainAsync(req.Domain.Trim()).ConfigureAwait(false);
        await ctx.WriteJsonAsync(200, ToResponse(saved)).ConfigureAwait(false);
    }

    /// <summary><c>GET /api/local-domains</c> - list all.</summary>
    /// <param name="ctx">Request context.</param>
    public async System.Threading.Tasks.Task ListAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.Collections.Generic.IReadOnlyList<LocalDomainRow> all =
            await this.store.ListLocalDomainsAsync().ConfigureAwait(false);

        var responses = new System.Collections.Generic.List<LocalDomainResponse>(all.Count);
        foreach (LocalDomainRow d in all)
        {
            responses.Add(ToResponse(d));
        }
        await ctx.WriteJsonAsync(200, responses).ConfigureAwait(false);
    }

    /// <summary><c>DELETE /api/local-domains/{domain}</c> - remove.</summary>
    /// <param name="ctx">Request context.</param>
    /// <param name="domain">Domain from URL.</param>
    public async System.Threading.Tasks.Task DeleteAsync(RequestContext ctx, string domain)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.ArgumentNullException.ThrowIfNull(domain);

        bool removed = await this.store.DeleteLocalDomainAsync(domain).ConfigureAwait(false);
        if (!removed)
        {
            await ctx.WriteErrorAsync(404, "not_found", $"No local domain '{domain}'.").ConfigureAwait(false);
            return;
        }
        await ctx.WriteEmptyAsync(204).ConfigureAwait(false);
    }

    private static LocalDomainResponse ToResponse(LocalDomainRow row) => new()
    {
        Id = row.Id,
        Domain = row.Domain,
        CreatedAt = row.CreatedAt,
    };
}
