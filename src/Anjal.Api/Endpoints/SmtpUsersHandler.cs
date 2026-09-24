using Anjal.Api.Dto;
using Anjal.Store;

namespace Anjal.Api.Endpoints;

/// <summary>
/// Endpoint handlers for <c>/api/smtp-users</c>. Passwords are accepted
/// as plaintext in POST bodies, hashed via PBKDF2 before persisting, and
/// NEVER returned by GET responses (no hash field in the response DTO).
/// </summary>
public sealed class SmtpUsersHandler
{
    private readonly IMessageStore store;

    /// <summary>Construct.</summary>
    /// <param name="store">Backing store.</param>
    public SmtpUsersHandler(IMessageStore store)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    /// <summary><c>POST /api/smtp-users</c> - create or update.</summary>
    /// <param name="ctx">Request context.</param>
    public async System.Threading.Tasks.Task PostAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        string body = await ctx.ReadBodyAsync().ConfigureAwait(false);
        SmtpUserRequest? req;
        try
        {
            req = ApiJson.Deserialize<SmtpUserRequest>(body);
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
        if (string.IsNullOrWhiteSpace(req.Username))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "username is required.").ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(req.Password))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "password is required.").ConfigureAwait(false);
            return;
        }
        if (Anjal.Smtp.PasswordPolicy.Check(req.Password, req.Username) is string weak)
        {
            await ctx.WriteErrorAsync(400, "invalid_request", weak).ConfigureAwait(false);
            return;
        }

        // Normalize allowed-from domains (lowercase, trim, dedupe).
        var domains = new System.Collections.Generic.List<string>();
        var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (string d in req.AllowedFromDomains)
        {
            string norm = d.Trim().ToLowerInvariant();
            if (norm.Length > 0 && seen.Add(norm))
            {
                domains.Add(norm);
            }
        }

        string hash = Anjal.Smtp.Pbkdf2Hasher.Hash(req.Password);

        SmtpUserRow saved = await this.store.UpsertSmtpUserAsync(new SmtpUserRow
        {
            Username = req.Username.Trim(),
            PasswordPbkdf2 = hash,
            AllowedFromDomains = domains,
            Enabled = req.Enabled,
        }).ConfigureAwait(false);

        await ctx.WriteJsonAsync(200, ToResponse(saved)).ConfigureAwait(false);
    }

    /// <summary><c>GET /api/smtp-users</c> - list users (hashes never returned).</summary>
    /// <param name="ctx">Request context.</param>
    public async System.Threading.Tasks.Task ListAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.Collections.Generic.IReadOnlyList<SmtpUserRow> all =
            await this.store.ListSmtpUsersAsync().ConfigureAwait(false);

        var responses = new System.Collections.Generic.List<SmtpUserResponse>(all.Count);
        foreach (SmtpUserRow u in all)
        {
            responses.Add(ToResponse(u));
        }
        await ctx.WriteJsonAsync(200, responses).ConfigureAwait(false);
    }

    /// <summary><c>DELETE /api/smtp-users/{username}</c> - remove.</summary>
    /// <param name="ctx">Request context.</param>
    /// <param name="username">Username from URL.</param>
    public async System.Threading.Tasks.Task DeleteAsync(RequestContext ctx, string username)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.ArgumentNullException.ThrowIfNull(username);

        bool removed = await this.store.DeleteSmtpUserAsync(username).ConfigureAwait(false);
        if (!removed)
        {
            await ctx.WriteErrorAsync(404, "not_found", $"No SMTP user '{username}'.").ConfigureAwait(false);
            return;
        }
        await ctx.WriteEmptyAsync(204).ConfigureAwait(false);
    }

    private static SmtpUserResponse ToResponse(SmtpUserRow row) => new()
    {
        Id = row.Id,
        Username = row.Username,
        AllowedFromDomains = new System.Collections.Generic.List<string>(row.AllowedFromDomains),
        Enabled = row.Enabled,
        UpdatedAt = row.UpdatedAt,
    };
}
