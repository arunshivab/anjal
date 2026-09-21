using Anjal.Api.Dto;
using Anjal.Store;

namespace Anjal.Api.Endpoints;

/// <summary>
/// Endpoint handlers for <c>/api/dkim-keys</c>. Private keys are accepted
/// in request bodies but NEVER returned in responses.
/// </summary>
public sealed class DkimKeysHandler
{
    private readonly IMessageStore store;

    /// <summary>Construct.</summary>
    /// <param name="store">Backing store.</param>
    public DkimKeysHandler(IMessageStore store)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    /// <summary><c>POST /api/dkim-keys</c> - upload or rotate.</summary>
    /// <param name="ctx">Request context.</param>
    public async System.Threading.Tasks.Task PostAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        string body = await ctx.ReadBodyAsync().ConfigureAwait(false);
        DkimKeyRequest? req;
        try
        {
            req = ApiJson.Deserialize<DkimKeyRequest>(body);
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
        if (string.IsNullOrWhiteSpace(req.Selector))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "selector is required.").ConfigureAwait(false);
            return;
        }
        // Fail closed: without a key-encryption key the private key would sit
        // in the database in plaintext. Refuse unless that was chosen
        // deliberately.
        if (System.Environment.GetEnvironmentVariable("ANJAL_KEK") is not { Length: > 0 } &&
            !string.Equals(System.Environment.GetEnvironmentVariable("ANJAL_ALLOW_PLAINTEXT_KEYS"), "true", System.StringComparison.OrdinalIgnoreCase) &&
            this.store is PostgresMessageStore)
        {
            await ctx.WriteErrorAsync(409, "kek_required", "Set ANJAL_KEK so DKIM keys are stored encrypted (openssl rand -base64 32), then restart.").ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(req.PrivateKeyPem) ||
            !req.PrivateKeyPem.Contains("-----BEGIN", System.StringComparison.Ordinal))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "privateKeyPem must be a PEM-encoded key.").ConfigureAwait(false);
            return;
        }

        // Validate the PEM by trying to import it - reject early rather than
        // discover the key is broken at the next outbound send.
        try
        {
            using var rsa = System.Security.Cryptography.RSA.Create();
            rsa.ImportFromPem(req.PrivateKeyPem);
        }
        catch (System.Exception ex) when (ex is System.ArgumentException || ex is System.Security.Cryptography.CryptographicException)
        {
            await ctx.WriteErrorAsync(400, "invalid_request", $"privateKeyPem failed to import: {ex.Message}").ConfigureAwait(false);
            return;
        }

        DkimKeyRow saved = await this.store.UpsertDkimKeyAsync(new DkimKeyRow
        {
            Domain = req.Domain,
            Selector = req.Selector,
            PrivateKeyPem = req.PrivateKeyPem,
        }).ConfigureAwait(false);

        await ctx.WriteJsonAsync(200, ToResponse(saved)).ConfigureAwait(false);
    }

    /// <summary><c>GET /api/dkim-keys</c> - list metadata (never returns private keys).</summary>
    /// <param name="ctx">Request context.</param>
    public async System.Threading.Tasks.Task ListAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.Collections.Generic.IReadOnlyList<DkimKeyRow> all =
            await this.store.ListDkimKeysAsync().ConfigureAwait(false);

        var responses = new System.Collections.Generic.List<DkimKeyResponse>(all.Count);
        foreach (DkimKeyRow k in all)
        {
            responses.Add(ToResponse(k));
        }
        await ctx.WriteJsonAsync(200, responses).ConfigureAwait(false);
    }

    /// <summary><c>DELETE /api/dkim-keys/{domain}</c> - remove.</summary>
    /// <param name="ctx">Request context.</param>
    /// <param name="domain">Sender domain from URL.</param>
    public async System.Threading.Tasks.Task DeleteAsync(RequestContext ctx, string domain)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.ArgumentNullException.ThrowIfNull(domain);

        bool removed = await this.store.DeleteDkimKeyAsync(domain).ConfigureAwait(false);
        if (!removed)
        {
            await ctx.WriteErrorAsync(404, "not_found", $"No DKIM key for domain '{domain}'.").ConfigureAwait(false);
            return;
        }
        await ctx.WriteEmptyAsync(204).ConfigureAwait(false);
    }

    private static DkimKeyResponse ToResponse(DkimKeyRow row) => new()
    {
        Id = row.Id,
        Domain = row.Domain,
        Selector = row.Selector,
        UpdatedAt = row.UpdatedAt,
    };
}
