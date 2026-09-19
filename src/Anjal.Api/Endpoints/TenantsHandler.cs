using Anjal.Api.Dto;
using Anjal.Store;

namespace Anjal.Api.Endpoints;

/// <summary>
/// Endpoint handlers for <c>/api/tenants</c> and <c>/api/tenant-domains</c>.
/// Tenant creation is admin-API-only: there is no self-service signup and
/// no domain-verification flow in this release - registering a domain
/// through this API marks it verified.
/// </summary>
public sealed class TenantsHandler
{
    private readonly IMailboxStore store;

    /// <summary>Construct.</summary>
    /// <param name="store">Backing store.</param>
    public TenantsHandler(IMailboxStore store)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    /// <summary>
    /// Validate a tenant slug: 1-63 characters of lowercase ASCII letters,
    /// digits and hyphens, not starting or ending with a hyphen.
    /// </summary>
    /// <param name="slug">The slug to check.</param>
    public static bool IsValidSlug(string slug)
    {
        System.ArgumentNullException.ThrowIfNull(slug);
        if (slug.Length == 0 || slug.Length > 63 || slug[0] == '-' || slug[slug.Length - 1] == '-')
        {
            return false;
        }
        foreach (char c in slug)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary><c>POST /api/tenants</c> - create or update.</summary>
    /// <param name="ctx">Request context.</param>
    public async System.Threading.Tasks.Task PostAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        string body = await ctx.ReadBodyAsync().ConfigureAwait(false);
        TenantRequest? req;
        try
        {
            req = ApiJson.Deserialize<TenantRequest>(body);
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
        string slug = req.Slug.Trim().ToLowerInvariant();
        if (!IsValidSlug(slug))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "slug must be 1-63 lowercase letters, digits or hyphens.").ConfigureAwait(false);
            return;
        }

        if (req.SpamThreshold is int st && st < 0)
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "spamThreshold must be 0 or more.").ConfigureAwait(false);
            return;
        }

        TenantRow? current = await this.store.GetTenantAsync(slug).ConfigureAwait(false);
        TenantRow saved = await this.store.UpsertTenantAsync(new TenantRow
        {
            Slug = slug,
            DisplayName = req.DisplayName.Trim(),
            Enabled = req.Enabled,
            SpamThreshold = req.SpamThreshold ?? current?.SpamThreshold ?? TenantRow.DefaultSpamThreshold,
        }).ConfigureAwait(false);
        await ctx.WriteJsonAsync(200, ToResponse(saved)).ConfigureAwait(false);
    }

    /// <summary><c>GET /api/tenants/{slug}/sender-rules</c> - list allow/block rules.</summary>
    /// <param name="ctx">Request context.</param>
    /// <param name="slug">Slug from URL.</param>
    public async System.Threading.Tasks.Task ListSenderRulesAsync(RequestContext ctx, string slug)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.ArgumentNullException.ThrowIfNull(slug);
        TenantRow? tenant = await this.store.GetTenantAsync(slug).ConfigureAwait(false);
        if (tenant is null)
        {
            await ctx.WriteErrorAsync(404, "not_found", $"No tenant '{slug}'.").ConfigureAwait(false);
            return;
        }
        System.Collections.Generic.IReadOnlyList<SenderRuleRow> rules = await this.store.ListSenderRulesAsync(tenant.Id).ConfigureAwait(false);
        var responses = new System.Collections.Generic.List<SenderRuleResponse>(rules.Count);
        foreach (SenderRuleRow r in rules)
        {
            responses.Add(ToResponse(r));
        }
        await ctx.WriteJsonAsync(200, responses).ConfigureAwait(false);
    }

    /// <summary><c>POST /api/tenants/{slug}/sender-rules</c> - create or replace a rule.</summary>
    /// <param name="ctx">Request context.</param>
    /// <param name="slug">Slug from URL.</param>
    public async System.Threading.Tasks.Task PostSenderRuleAsync(RequestContext ctx, string slug)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.ArgumentNullException.ThrowIfNull(slug);
        TenantRow? tenant = await this.store.GetTenantAsync(slug).ConfigureAwait(false);
        if (tenant is null)
        {
            await ctx.WriteErrorAsync(404, "not_found", $"No tenant '{slug}'.").ConfigureAwait(false);
            return;
        }
        string body = await ctx.ReadBodyAsync().ConfigureAwait(false);
        SenderRuleRequest? req;
        try
        {
            req = ApiJson.Deserialize<SenderRuleRequest>(body);
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
        string pattern = req.Pattern.Trim().ToLowerInvariant();
        if (!IsValidSenderPattern(pattern))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "pattern must be local@domain or @domain.").ConfigureAwait(false);
            return;
        }
        SenderRuleAction action;
        if (string.Equals(req.Action, "allow", System.StringComparison.OrdinalIgnoreCase))
        {
            action = SenderRuleAction.Allow;
        }
        else if (string.Equals(req.Action, "block", System.StringComparison.OrdinalIgnoreCase))
        {
            action = SenderRuleAction.Block;
        }
        else
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "action must be allow or block.").ConfigureAwait(false);
            return;
        }

        SenderRuleRow saved = await this.store.UpsertSenderRuleAsync(new SenderRuleRow
        {
            TenantId = tenant.Id,
            Pattern = pattern,
            Action = action,
        }).ConfigureAwait(false);
        await ctx.WriteJsonAsync(200, ToResponse(saved)).ConfigureAwait(false);
    }

    /// <summary><c>DELETE /api/tenants/{slug}/sender-rules/{pattern}</c> - remove a rule.</summary>
    /// <param name="ctx">Request context.</param>
    /// <param name="slug">Slug from URL.</param>
    /// <param name="pattern">Pattern from URL (URL-decoded).</param>
    public async System.Threading.Tasks.Task DeleteSenderRuleAsync(RequestContext ctx, string slug, string pattern)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.ArgumentNullException.ThrowIfNull(slug);
        System.ArgumentNullException.ThrowIfNull(pattern);
        TenantRow? tenant = await this.store.GetTenantAsync(slug).ConfigureAwait(false);
        if (tenant is null)
        {
            await ctx.WriteErrorAsync(404, "not_found", $"No tenant '{slug}'.").ConfigureAwait(false);
            return;
        }
        bool removed = await this.store.DeleteSenderRuleAsync(tenant.Id, pattern).ConfigureAwait(false);
        if (!removed)
        {
            await ctx.WriteErrorAsync(404, "not_found", $"No sender rule '{pattern}'.").ConfigureAwait(false);
            return;
        }
        await ctx.WriteEmptyAsync(204).ConfigureAwait(false);
    }

    /// <summary>A pattern is <c>local@domain</c> or <c>@domain</c>, with no whitespace.</summary>
    /// <param name="pattern">Lowercased pattern.</param>
    public static bool IsValidSenderPattern(string pattern)
    {
        System.ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Length < 2 || pattern.Contains(' ', System.StringComparison.Ordinal) || pattern.Contains('/', System.StringComparison.Ordinal))
        {
            return false;
        }
        int at = pattern.IndexOf('@', System.StringComparison.Ordinal);
        if (at < 0 || at != pattern.LastIndexOf('@') || at == pattern.Length - 1)
        {
            return false;
        }
        return pattern.Substring(at + 1).Contains('.', System.StringComparison.Ordinal);
    }

    /// <summary><c>GET /api/tenants</c> - list.</summary>
    /// <param name="ctx">Request context.</param>
    public async System.Threading.Tasks.Task ListAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.Collections.Generic.IReadOnlyList<TenantRow> all = await this.store.ListTenantsAsync().ConfigureAwait(false);
        var responses = new System.Collections.Generic.List<TenantResponse>(all.Count);
        foreach (TenantRow t in all)
        {
            responses.Add(ToResponse(t));
        }
        await ctx.WriteJsonAsync(200, responses).ConfigureAwait(false);
    }

    /// <summary><c>GET /api/tenants/{slug}</c> - fetch one.</summary>
    /// <param name="ctx">Request context.</param>
    /// <param name="slug">Slug from URL.</param>
    public async System.Threading.Tasks.Task GetAsync(RequestContext ctx, string slug)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.ArgumentNullException.ThrowIfNull(slug);
        TenantRow? found = await this.store.GetTenantAsync(slug).ConfigureAwait(false);
        if (found is null)
        {
            await ctx.WriteErrorAsync(404, "not_found", $"No tenant '{slug}'.").ConfigureAwait(false);
            return;
        }
        await ctx.WriteJsonAsync(200, ToResponse(found)).ConfigureAwait(false);
    }

    /// <summary><c>DELETE /api/tenants/{slug}</c> - remove a tenant and everything under it (index only; Maildir files stay on disk).</summary>
    /// <param name="ctx">Request context.</param>
    /// <param name="slug">Slug from URL.</param>
    public async System.Threading.Tasks.Task DeleteAsync(RequestContext ctx, string slug)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.ArgumentNullException.ThrowIfNull(slug);
        bool removed = await this.store.DeleteTenantAsync(slug).ConfigureAwait(false);
        if (!removed)
        {
            await ctx.WriteErrorAsync(404, "not_found", $"No tenant '{slug}'.").ConfigureAwait(false);
            return;
        }
        await ctx.WriteEmptyAsync(204).ConfigureAwait(false);
    }

    /// <summary><c>POST /api/tenant-domains</c> - register a domain to a tenant.</summary>
    /// <param name="ctx">Request context.</param>
    public async System.Threading.Tasks.Task PostDomainAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        string body = await ctx.ReadBodyAsync().ConfigureAwait(false);
        TenantDomainRequest? req;
        try
        {
            req = ApiJson.Deserialize<TenantDomainRequest>(body);
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
        if (string.IsNullOrWhiteSpace(req.TenantSlug))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "tenantSlug is required.").ConfigureAwait(false);
            return;
        }
        string domain = req.Domain.Trim().ToLowerInvariant();
        if (domain.Length == 0 || domain.Contains('@', System.StringComparison.Ordinal) || domain.Contains(' ', System.StringComparison.Ordinal))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "domain is required and must be a bare domain name.").ConfigureAwait(false);
            return;
        }

        TenantRow? tenant = await this.store.GetTenantAsync(req.TenantSlug.Trim()).ConfigureAwait(false);
        if (tenant is null)
        {
            await ctx.WriteErrorAsync(404, "not_found", $"No tenant '{req.TenantSlug}'.").ConfigureAwait(false);
            return;
        }

        TenantDomainRow saved = await this.store.UpsertTenantDomainAsync(new TenantDomainRow
        {
            TenantId = tenant.Id,
            Domain = domain,
            Verified = true,
        }).ConfigureAwait(false);
        await ctx.WriteJsonAsync(200, ToResponse(saved, tenant.Slug)).ConfigureAwait(false);
    }

    /// <summary><c>GET /api/tenant-domains[?tenant=slug]</c> - list domains.</summary>
    /// <param name="ctx">Request context.</param>
    public async System.Threading.Tasks.Task ListDomainsAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.Guid? tenantId = null;
        string? filter = ctx.Query("tenant");
        if (!string.IsNullOrWhiteSpace(filter))
        {
            TenantRow? tenant = await this.store.GetTenantAsync(filter).ConfigureAwait(false);
            if (tenant is null)
            {
                await ctx.WriteErrorAsync(404, "not_found", $"No tenant '{filter}'.").ConfigureAwait(false);
                return;
            }
            tenantId = tenant.Id;
        }

        System.Collections.Generic.IReadOnlyList<TenantDomainRow> all = await this.store.ListTenantDomainsAsync(tenantId).ConfigureAwait(false);
        var slugs = new System.Collections.Generic.Dictionary<System.Guid, string>();
        foreach (TenantRow t in await this.store.ListTenantsAsync().ConfigureAwait(false))
        {
            slugs[t.Id] = t.Slug;
        }
        var responses = new System.Collections.Generic.List<TenantDomainResponse>(all.Count);
        foreach (TenantDomainRow d in all)
        {
            responses.Add(ToResponse(d, slugs.TryGetValue(d.TenantId, out string? s) ? s : string.Empty));
        }
        await ctx.WriteJsonAsync(200, responses).ConfigureAwait(false);
    }

    /// <summary><c>DELETE /api/tenant-domains/{domain}</c> - unregister.</summary>
    /// <param name="ctx">Request context.</param>
    /// <param name="domain">Domain from URL.</param>
    public async System.Threading.Tasks.Task DeleteDomainAsync(RequestContext ctx, string domain)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.ArgumentNullException.ThrowIfNull(domain);
        bool removed = await this.store.DeleteTenantDomainAsync(domain).ConfigureAwait(false);
        if (!removed)
        {
            await ctx.WriteErrorAsync(404, "not_found", $"No tenant domain '{domain}'.").ConfigureAwait(false);
            return;
        }
        await ctx.WriteEmptyAsync(204).ConfigureAwait(false);
    }

    private static TenantResponse ToResponse(TenantRow t) => new()
    {
        Id = t.Id,
        Slug = t.Slug,
        DisplayName = t.DisplayName,
        Enabled = t.Enabled,
        SpamThreshold = t.SpamThreshold,
        CreatedAt = t.CreatedAt,
    };

    private static SenderRuleResponse ToResponse(SenderRuleRow r) => new()
    {
        Id = r.Id,
        Pattern = r.Pattern,
        Action = r.Action == SenderRuleAction.Block ? "block" : "allow",
        CreatedAt = r.CreatedAt,
    };

    private static TenantDomainResponse ToResponse(TenantDomainRow d, string tenantSlug) => new()
    {
        Id = d.Id,
        TenantId = d.TenantId,
        TenantSlug = tenantSlug,
        Domain = d.Domain,
        Verified = d.Verified,
        CreatedAt = d.CreatedAt,
    };
}
