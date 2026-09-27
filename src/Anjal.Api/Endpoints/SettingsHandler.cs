using Anjal.Api.Dto;
using Anjal.Store;

namespace Anjal.Api.Endpoints;

/// <summary>
/// <c>/api/settings</c> (v1.0.0-rc.7): the non-secret settings of the server
/// and the webmail, kept in the database so a rebuild from backup restores
/// them. Every change is audited by the API; it takes effect when the
/// service restarts. Secrets are refused.
/// </summary>
public sealed class SettingsHandler
{
    private readonly IMessageStore store;

    /// <summary>Construct with the backing store.</summary>
    /// <param name="store">Store holding the settings.</param>
    public SettingsHandler(IMessageStore store)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    /// <summary><c>GET /api/settings</c> - both scopes.</summary>
    /// <param name="ctx">Request context.</param>
    /// <returns>A task.</returns>
    public async System.Threading.Tasks.Task ListAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        var response = new SettingsListResponse();
        foreach (SettingRow r in await this.store.ListSettingsAsync(SettingRow.ServerScope).ConfigureAwait(false))
        {
            response.Server.Add(ToResponse(r));
        }
        foreach (SettingRow r in await this.store.ListSettingsAsync(SettingRow.WebmailScope).ConfigureAwait(false))
        {
            response.Webmail.Add(ToResponse(r));
        }
        await ctx.WriteJsonAsync(200, response).ConfigureAwait(false);
    }

    /// <summary><c>PUT /api/settings/{scope}/{key}</c> - store a value.</summary>
    /// <param name="ctx">Request context.</param>
    /// <param name="scope">"server" or "webmail".</param>
    /// <param name="key">The setting's name.</param>
    /// <returns>A task.</returns>
    public async System.Threading.Tasks.Task PutAsync(RequestContext ctx, string scope, string key)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.ArgumentNullException.ThrowIfNull(scope);
        System.ArgumentNullException.ThrowIfNull(key);
        if (!await CheckAsync(ctx, scope, key).ConfigureAwait(false))
        {
            return;
        }
        SettingRequest? req;
        try
        {
            req = ApiJson.Deserialize<SettingRequest>(await ctx.ReadBodyAsync().ConfigureAwait(false));
        }
        catch (System.Text.Json.JsonException ex)
        {
            await ctx.WriteErrorAsync(400, "invalid_json", ex.Message).ConfigureAwait(false);
            return;
        }
        if (req is null || req.Value.Length == 0 || req.Value.Length > 4096 || req.Value.Contains('\n', System.StringComparison.Ordinal))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "value must be 1-4096 characters on one line.").ConfigureAwait(false);
            return;
        }
        SettingRow saved = await this.store.UpsertSettingAsync(new SettingRow { Scope = scope, Key = key, Value = req.Value, UpdatedBy = "api" }).ConfigureAwait(false);
        await ctx.WriteJsonAsync(200, ToResponse(saved)).ConfigureAwait(false);
    }

    /// <summary><c>DELETE /api/settings/{scope}/{key}</c> - remove a stored value.</summary>
    /// <param name="ctx">Request context.</param>
    /// <param name="scope">"server" or "webmail".</param>
    /// <param name="key">The setting's name.</param>
    /// <returns>A task.</returns>
    public async System.Threading.Tasks.Task DeleteAsync(RequestContext ctx, string scope, string key)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.ArgumentNullException.ThrowIfNull(scope);
        System.ArgumentNullException.ThrowIfNull(key);
        if (!await CheckAsync(ctx, scope, key).ConfigureAwait(false))
        {
            return;
        }
        if (!await this.store.DeleteSettingAsync(scope, key).ConfigureAwait(false))
        {
            await ctx.WriteErrorAsync(404, "not_found", $"{key} is not stored for {scope}.").ConfigureAwait(false);
            return;
        }
        await ctx.WriteEmptyAsync(204).ConfigureAwait(false);
    }

    private static async System.Threading.Tasks.Task<bool> CheckAsync(RequestContext ctx, string scope, string key)
    {
        if (scope != SettingRow.ServerScope && scope != SettingRow.WebmailScope)
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "scope must be server or webmail.").ConfigureAwait(false);
            return false;
        }
        if (StoredSettings.IsSecret(key))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", $"{key} is a secret; secrets stay in /etc/anjal and on the custody forms, never in the database.").ConfigureAwait(false);
            return false;
        }
        if (!StoredSettings.IsStorable(key))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "key must be an ANJAL_ setting name.").ConfigureAwait(false);
            return false;
        }
        return true;
    }

    private static SettingResponse ToResponse(SettingRow r) => new()
    {
        Scope = r.Scope,
        Key = r.Key,
        Value = r.Value,
        UpdatedAt = r.UpdatedAt,
        UpdatedBy = r.UpdatedBy,
    };
}
