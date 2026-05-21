using System.Net;
using System.Security.Cryptography;
using System.Text;
using Anjal.Api.Endpoints;
using Anjal.Store;

namespace Anjal.Api;

/// <summary>
/// HTTP API server. Wraps <see cref="HttpListener"/>, applies bearer
/// authentication, and dispatches by HTTP method + path to the
/// appropriate endpoint handler.
/// </summary>
public sealed class ApiServer : System.IDisposable
{
    private readonly ApiOptions options;
    private readonly HttpListener listener;
    private readonly RoutingRulesHandler routingRules;
    private readonly TagGrantsHandler tagGrants;
    private readonly OutboundHandler outbound;
    private readonly InboundHandler inbound;
    private readonly OutboundTlsPoliciesHandler tlsPolicies;
    private readonly DkimKeysHandler dkimKeys;
    private readonly SmtpUsersHandler smtpUsers;
    private readonly LocalDomainsHandler localDomains;
    private readonly System.Action<string>? log;
    private bool started;
    private bool disposed;

    /// <summary>
    /// Construct an API server.
    /// </summary>
    /// <param name="options">Configuration.</param>
    /// <param name="store">Backing store, passed to all handlers.</param>
    /// <param name="log">Optional log callback. One line per served request.</param>
    public ApiServer(ApiOptions options, IMessageStore store, System.Action<string>? log = null)
    {
        System.ArgumentNullException.ThrowIfNull(options);
        System.ArgumentNullException.ThrowIfNull(store);
        this.options = options;
        this.log = log;

        this.routingRules = new RoutingRulesHandler(store);
        this.tagGrants = new TagGrantsHandler(store);
        this.outbound = new OutboundHandler(store);
        this.inbound = new InboundHandler(store);
        this.tlsPolicies = new OutboundTlsPoliciesHandler(store);
        this.dkimKeys = new DkimKeysHandler(store);
        this.smtpUsers = new SmtpUsersHandler(store);
        this.localDomains = new LocalDomainsHandler(store);

        this.listener = new HttpListener();
        // HttpListener takes a URI-style prefix like "http://127.0.0.1:8080/".
        // We bind to the specified address; the port is whatever the user set.
        string addr = options.BindAddress.ToString();
        if (options.BindAddress.Equals(IPAddress.Any))
        {
            addr = "+";
        }
        this.listener.Prefixes.Add($"http://{addr}:{options.Port}/");
    }

    /// <summary>
    /// The actual port the server bound to. Useful when configured with port 0.
    /// Read after <see cref="StartAsync"/> returns.
    /// </summary>
    public int BoundPort
    {
        get
        {
            // HttpListener doesn't expose the bound port directly when port 0
            // is requested - this is a known limitation. For tests we just use
            // a known fixed port. In production the port is explicit anyway.
            return this.options.Port;
        }
    }

    /// <summary>
    /// Start the accept loop. Returns a task that completes when the loop exits.
    /// </summary>
    /// <param name="ct">Cancellation token controlling the loop's lifetime.</param>
    public System.Threading.Tasks.Task StartAsync(System.Threading.CancellationToken ct = default)
    {
        if (this.started)
        {
            throw new System.InvalidOperationException("ApiServer already started.");
        }
        this.started = true;
        this.listener.Start();

        // HttpListener.GetContextAsync does not honour CancellationToken.
        // Register a callback that stops the listener on cancellation,
        // which causes GetContextAsync to throw HttpListenerException -
        // we catch that in the accept loop to exit cleanly.
        ct.Register(() =>
        {
            try
            {
                this.listener.Stop();
            }
#pragma warning disable CA1031
            catch (System.Exception)
            {
                // Already stopped or disposed; safe to swallow.
            }
#pragma warning restore CA1031
        });

        return this.AcceptLoopAsync(ct);
    }

    private async System.Threading.Tasks.Task AcceptLoopAsync(System.Threading.CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await this.listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException)
                {
                    return;
                }
                catch (System.ObjectDisposedException)
                {
                    return;
                }

                _ = System.Threading.Tasks.Task.Run(() => this.HandleRequestAsync(context, ct), ct);
            }
        }
        finally
        {
            try
            {
                this.listener.Stop();
            }
            catch (System.ObjectDisposedException)
            {
                // Already stopped - swallow.
            }
        }
    }

    private async System.Threading.Tasks.Task HandleRequestAsync(HttpListenerContext httpContext, System.Threading.CancellationToken ct)
    {
        var ctx = new RequestContext(httpContext, this.options.MaxBodyBytes);
        try
        {
            this.log?.Invoke($"{ctx.Method} {ctx.Path}");

            // Auth check first, before anything else.
            if (!this.CheckAuth(ctx))
            {
                await ctx.WriteErrorAsync(401, "unauthorized", "Missing or invalid bearer token.").ConfigureAwait(false);
                return;
            }

            await this.DispatchAsync(ctx).ConfigureAwait(false);
        }
        catch (System.IO.InvalidDataException ex)
        {
            await TrySafeErrorAsync(ctx, 413, "payload_too_large", ex.Message).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Intentional: any error must produce a response, not crash the server.
        catch (System.Exception ex)
        {
            this.log?.Invoke($"  500 - {ex.GetType().Name}: {ex.Message}");
            await TrySafeErrorAsync(ctx, 500, "internal_error", ex.Message).ConfigureAwait(false);
        }
#pragma warning restore CA1031
    }

    private static async System.Threading.Tasks.Task TrySafeErrorAsync(RequestContext ctx, int code, string error, string message)
    {
        try
        {
            await ctx.WriteErrorAsync(code, error, message).ConfigureAwait(false);
        }
#pragma warning disable CA1031
        catch (System.Exception)
        {
            // Response already sent or stream closed. Nothing to do.
        }
#pragma warning restore CA1031
    }

    private bool CheckAuth(RequestContext ctx)
    {
        // Empty configured token disables auth (test-only mode).
        if (string.IsNullOrEmpty(this.options.BearerToken))
        {
            return true;
        }

        string header = ctx.AuthorizationHeader;
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, System.StringComparison.Ordinal))
        {
            return false;
        }
        string presented = header.Substring(prefix.Length);
        return ConstantTimeEquals(presented, this.options.BearerToken);
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        // Compare in time independent of where the first mismatch falls.
        byte[] aBytes = Encoding.UTF8.GetBytes(a);
        byte[] bBytes = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }

    private async System.Threading.Tasks.Task DispatchAsync(RequestContext ctx)
    {
        // /api/routing-rules         POST, GET
        // /api/routing-rules/{id}    DELETE
        // /api/tag-grants            POST
        // /api/outbound              POST
        // /api/outbound/{id}         GET
        // /api/inbound/{id}          GET
        string path = ctx.Path;

        if (path.Equals("/api/routing-rules", System.StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/routing-rules/", System.StringComparison.OrdinalIgnoreCase))
        {
            switch (ctx.Method)
            {
                case "POST": await this.routingRules.PostAsync(ctx).ConfigureAwait(false); return;
                case "GET": await this.routingRules.ListAsync(ctx).ConfigureAwait(false); return;
                default:
                    await ctx.WriteErrorAsync(405, "method_not_allowed", $"{ctx.Method} not allowed on {path}.").ConfigureAwait(false);
                    return;
            }
        }

        const string routingRulePrefix = "/api/routing-rules/";
        if (path.StartsWith(routingRulePrefix, System.StringComparison.OrdinalIgnoreCase))
        {
            string localPart = path.Substring(routingRulePrefix.Length);
            if (ctx.Method == "DELETE" && localPart.Length > 0)
            {
                await this.routingRules.DeleteAsync(ctx, localPart).ConfigureAwait(false);
                return;
            }
            await ctx.WriteErrorAsync(405, "method_not_allowed", $"{ctx.Method} not allowed on {path}.").ConfigureAwait(false);
            return;
        }

        if (path.Equals("/api/tag-grants", System.StringComparison.OrdinalIgnoreCase))
        {
            if (ctx.Method == "POST")
            {
                await this.tagGrants.PostAsync(ctx).ConfigureAwait(false);
                return;
            }
            await ctx.WriteErrorAsync(405, "method_not_allowed", $"{ctx.Method} not allowed on {path}.").ConfigureAwait(false);
            return;
        }

        if (path.Equals("/api/outbound", System.StringComparison.OrdinalIgnoreCase))
        {
            if (ctx.Method == "POST")
            {
                await this.outbound.PostAsync(ctx).ConfigureAwait(false);
                return;
            }
            await ctx.WriteErrorAsync(405, "method_not_allowed", $"{ctx.Method} not allowed on {path}.").ConfigureAwait(false);
            return;
        }

        const string outboundPrefix = "/api/outbound/";
        if (path.StartsWith(outboundPrefix, System.StringComparison.OrdinalIgnoreCase))
        {
            string id = path.Substring(outboundPrefix.Length);
            if (ctx.Method == "GET" && id.Length > 0)
            {
                await this.outbound.GetAsync(ctx, id).ConfigureAwait(false);
                return;
            }
            await ctx.WriteErrorAsync(405, "method_not_allowed", $"{ctx.Method} not allowed on {path}.").ConfigureAwait(false);
            return;
        }

        const string inboundPrefix = "/api/inbound/";
        if (path.StartsWith(inboundPrefix, System.StringComparison.OrdinalIgnoreCase))
        {
            string id = path.Substring(inboundPrefix.Length);
            if (ctx.Method == "GET" && id.Length > 0)
            {
                await this.inbound.GetAsync(ctx, id).ConfigureAwait(false);
                return;
            }
            await ctx.WriteErrorAsync(405, "method_not_allowed", $"{ctx.Method} not allowed on {path}.").ConfigureAwait(false);
            return;
        }

        if (path.Equals("/api/outbound-tls-policies", System.StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/outbound-tls-policies/", System.StringComparison.OrdinalIgnoreCase))
        {
            switch (ctx.Method)
            {
                case "POST": await this.tlsPolicies.PostAsync(ctx).ConfigureAwait(false); return;
                case "GET": await this.tlsPolicies.ListAsync(ctx).ConfigureAwait(false); return;
                default:
                    await ctx.WriteErrorAsync(405, "method_not_allowed", $"{ctx.Method} not allowed on {path}.").ConfigureAwait(false);
                    return;
            }
        }

        const string tlsPolicyPrefix = "/api/outbound-tls-policies/";
        if (path.StartsWith(tlsPolicyPrefix, System.StringComparison.OrdinalIgnoreCase))
        {
            string domain = path.Substring(tlsPolicyPrefix.Length);
            if (ctx.Method == "DELETE" && domain.Length > 0)
            {
                await this.tlsPolicies.DeleteAsync(ctx, domain).ConfigureAwait(false);
                return;
            }
            await ctx.WriteErrorAsync(405, "method_not_allowed", $"{ctx.Method} not allowed on {path}.").ConfigureAwait(false);
            return;
        }

        if (path.Equals("/api/dkim-keys", System.StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/dkim-keys/", System.StringComparison.OrdinalIgnoreCase))
        {
            switch (ctx.Method)
            {
                case "POST": await this.dkimKeys.PostAsync(ctx).ConfigureAwait(false); return;
                case "GET": await this.dkimKeys.ListAsync(ctx).ConfigureAwait(false); return;
                default:
                    await ctx.WriteErrorAsync(405, "method_not_allowed", $"{ctx.Method} not allowed on {path}.").ConfigureAwait(false);
                    return;
            }
        }

        const string dkimPrefix = "/api/dkim-keys/";
        if (path.StartsWith(dkimPrefix, System.StringComparison.OrdinalIgnoreCase))
        {
            string domain = path.Substring(dkimPrefix.Length);
            if (ctx.Method == "DELETE" && domain.Length > 0)
            {
                await this.dkimKeys.DeleteAsync(ctx, domain).ConfigureAwait(false);
                return;
            }
            await ctx.WriteErrorAsync(405, "method_not_allowed", $"{ctx.Method} not allowed on {path}.").ConfigureAwait(false);
            return;
        }

        if (path.Equals("/api/smtp-users", System.StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/smtp-users/", System.StringComparison.OrdinalIgnoreCase))
        {
            switch (ctx.Method)
            {
                case "POST": await this.smtpUsers.PostAsync(ctx).ConfigureAwait(false); return;
                case "GET": await this.smtpUsers.ListAsync(ctx).ConfigureAwait(false); return;
                default:
                    await ctx.WriteErrorAsync(405, "method_not_allowed", $"{ctx.Method} not allowed on {path}.").ConfigureAwait(false);
                    return;
            }
        }

        const string smtpUsersPrefix = "/api/smtp-users/";
        if (path.StartsWith(smtpUsersPrefix, System.StringComparison.OrdinalIgnoreCase))
        {
            string username = path.Substring(smtpUsersPrefix.Length);
            if (ctx.Method == "DELETE" && username.Length > 0)
            {
                await this.smtpUsers.DeleteAsync(ctx, username).ConfigureAwait(false);
                return;
            }
            await ctx.WriteErrorAsync(405, "method_not_allowed", $"{ctx.Method} not allowed on {path}.").ConfigureAwait(false);
            return;
        }

        if (path.Equals("/api/local-domains", System.StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/local-domains/", System.StringComparison.OrdinalIgnoreCase))
        {
            switch (ctx.Method)
            {
                case "POST": await this.localDomains.PostAsync(ctx).ConfigureAwait(false); return;
                case "GET": await this.localDomains.ListAsync(ctx).ConfigureAwait(false); return;
                default:
                    await ctx.WriteErrorAsync(405, "method_not_allowed", $"{ctx.Method} not allowed on {path}.").ConfigureAwait(false);
                    return;
            }
        }

        const string localDomainsPrefix = "/api/local-domains/";
        if (path.StartsWith(localDomainsPrefix, System.StringComparison.OrdinalIgnoreCase))
        {
            string domain = path.Substring(localDomainsPrefix.Length);
            if (ctx.Method == "DELETE" && domain.Length > 0)
            {
                await this.localDomains.DeleteAsync(ctx, domain).ConfigureAwait(false);
                return;
            }
            await ctx.WriteErrorAsync(405, "method_not_allowed", $"{ctx.Method} not allowed on {path}.").ConfigureAwait(false);
            return;
        }

        await ctx.WriteErrorAsync(404, "not_found", $"No route matches {path}.").ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }
        this.disposed = true;
        try
        {
            this.listener.Close();
        }
        catch (System.ObjectDisposedException)
        {
            // Already closed.
        }
    }
}
