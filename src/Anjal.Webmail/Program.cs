using System.Globalization;
using System.Security.Claims;
using Anjal.Acme;
using Anjal.Mailbox;
using Anjal.Store;
using Anjal.Webmail.Components;
using Anjal.Webmail.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace Anjal.Webmail;

/// <summary>
/// Composition root for the Anjal webmail host. A separate process from
/// <c>Anjal.Server</c> that shares the same PostgreSQL database and
/// Maildir root, so the UI can be restarted or redeployed without
/// interrupting mail reception.
///
/// Environment variables:
///   ANJAL_WEBMAIL_BIND      - bind address, default "127.0.0.1"
///   ANJAL_WEBMAIL_PORT      - HTTP port, default 8080
///   ANJAL_HOSTNAME          - host name used in generated Message-IDs, default "anjal.localhost"
///   ANJAL_POSTGRES          - PostgreSQL connection string. If unset, an in-memory store is
///                             used (demo only - it is NOT shared with Anjal.Server).
///   ANJAL_MAILDIR_ROOT      - Maildir root shared with Anjal.Server.
///   ANJAL_WEBMAIL_SECURE    - "true" to mark the session cookie Secure. Implied when HTTPS is on.
///   ANJAL_WEBMAIL_HTTPS_PORT - HTTPS port (443 in production). 0 or unset disables HTTPS.
///                              Requires ANJAL_ACME_DOMAINS (the certificate comes from the
///                              ACME store) or ANJAL_TLS_CERT_PATH/ANJAL_TLS_KEY_PATH (a static PEM pair).
///   ANJAL_ACME_*            - see <see cref="AcmeEnvironment"/>. The webmail hosts the
///                              renewal service by default when domains are configured
///                              (ANJAL_ACME_HOST=false hands that to another process) and
///                              always answers HTTP-01 challenges on its HTTP listener.
///
/// With HTTPS on, the HTTP listener serves only ACME challenges and
/// redirects everything else to HTTPS; HSTS is sent on HTTPS responses.
/// Until the first certificate is issued the webmail serves plain HTTP
/// so a DNS mistake cannot lock you out - watch the log and
/// GET /api/acme on the server for the issuance result.
///
/// Command line:
///   --acme-renew-now        - request an immediate renewal and exit.
/// </summary>
public static class Program
{
    private const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;

    /// <summary>Entry point.</summary>
    /// <param name="args">Command-line arguments (passed through to the host builder).</param>
    public static async Task<int> Main(string[] args)
    {
        if (args is not null && Array.IndexOf(args, "--acme-renew-now") >= 0)
        {
            AcmeEnvironment env = AcmeEnvironment.Read(hostByDefault: true);
            new CertificateStore(env.Directory).RequestRenewal();
            Console.WriteLine($"Renewal requested; marker written to {env.Directory}. The hosting process will act on it within a minute.");
            return 0;
        }

        string bind = Environment.GetEnvironmentVariable("ANJAL_WEBMAIL_BIND") ?? "127.0.0.1";
        string portStr = Environment.GetEnvironmentVariable("ANJAL_WEBMAIL_PORT") ?? "8080";
        if (!int.TryParse(portStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port))
        {
            Console.Error.WriteLine($"Invalid ANJAL_WEBMAIL_PORT: {portStr}");
            return 1;
        }
        string? pg = Environment.GetEnvironmentVariable("ANJAL_POSTGRES");
        IMessageStore store = pg is null ? new InMemoryMessageStore() : new PostgresMessageStore(pg);
        string maildirRoot = Environment.GetEnvironmentVariable("ANJAL_MAILDIR_ROOT") ?? MaildirStore.DefaultRoot;
        string hostname = Environment.GetEnvironmentVariable("ANJAL_HOSTNAME") ?? "anjal.localhost";

        string httpsPortStr = Environment.GetEnvironmentVariable("ANJAL_WEBMAIL_HTTPS_PORT") ?? "0";
        if (!int.TryParse(httpsPortStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int httpsPort) || httpsPort < 0)
        {
            Console.Error.WriteLine($"Invalid ANJAL_WEBMAIL_HTTPS_PORT: {httpsPortStr}");
            return 1;
        }

        AcmeEnvironment acme = AcmeEnvironment.Read(hostByDefault: true);
        var tls = new TlsSettings
        {
            HttpsPort = httpsPort,
            Acme = acme,
            StaticCertPath = Environment.GetEnvironmentVariable("ANJAL_TLS_CERT_PATH"),
            StaticKeyPath = Environment.GetEnvironmentVariable("ANJAL_TLS_KEY_PATH"),
        };

        WebApplication app = CreateApp(args ?? Array.Empty<string>(), store, new MaildirStore(maildirRoot, hostname), hostname, $"http://{bind}:{port}", tls);
        Console.WriteLine($"Anjal webmail listening on http://{bind}:{port}/" + (httpsPort > 0 ? $" and https://{bind}:{httpsPort}/" : string.Empty));
        Console.WriteLine(pg is null ? "Using in-memory store (demo only)." : "Using PostgreSQL store.");
        Console.WriteLine($"Maildir root: {maildirRoot}");
        if (acme.Configured)
        {
            Console.WriteLine($"ACME: {string.Join(", ", acme.Domains)} via {acme.Options.DirectoryUrl}; store {acme.Directory}; " + (acme.Host ? "renewal hosted here." : "renewal hosted elsewhere."));
        }
        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>TLS-related settings for <see cref="CreateApp(string[], IMessageStore, IMaildirStore, string, string, TlsSettings?)"/>.</summary>
    public sealed class TlsSettings
    {
        /// <summary>HTTPS port; 0 disables HTTPS.</summary>
        public int HttpsPort { get; init; }

        /// <summary>ACME environment; when configured, the certificate comes from its store.</summary>
        public AcmeEnvironment? Acme { get; init; }

        /// <summary>Static certificate PEM (with or without the key), used when ACME is not configured.</summary>
        public string? StaticCertPath { get; init; }

        /// <summary>Static key PEM if not embedded in the certificate file.</summary>
        public string? StaticKeyPath { get; init; }

        /// <summary>Whether HTTPS is requested.</summary>
        public bool HttpsEnabled => this.HttpsPort > 0;
    }

    /// <summary>
    /// Build the web application. Exposed so tests can host the real
    /// pipeline against an in-memory store on an ephemeral port.
    /// </summary>
    /// <param name="args">Host builder arguments.</param>
    /// <param name="store">Store implementing both <see cref="IMessageStore"/> and <see cref="IMailboxStore"/>.</param>
    /// <param name="maildir">Filesystem store for message bodies.</param>
    /// <param name="hostName">Host name for generated Message-IDs.</param>
    /// <param name="url">Listen URL, e.g. <c>http://127.0.0.1:0</c> for an ephemeral port.</param>
    public static WebApplication CreateApp(string[] args, IMessageStore store, IMaildirStore maildir, string hostName, string url)
        => CreateApp(args, store, maildir, hostName, url, tls: null);

    /// <summary>
    /// Build the web application with optional HTTPS and ACME.
    /// </summary>
    /// <param name="args">Host builder arguments.</param>
    /// <param name="store">Store implementing both <see cref="IMessageStore"/> and <see cref="IMailboxStore"/>.</param>
    /// <param name="maildir">Filesystem store for message bodies.</param>
    /// <param name="hostName">Host name for generated Message-IDs.</param>
    /// <param name="url">HTTP listen URL, e.g. <c>http://127.0.0.1:0</c>.</param>
    /// <param name="tls">HTTPS/ACME settings, or null for HTTP only.</param>
    public static WebApplication CreateApp(string[] args, IMessageStore store, IMaildirStore maildir, string hostName, string url, TlsSettings? tls)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(maildir);
        ArgumentNullException.ThrowIfNull(hostName);
        ArgumentNullException.ThrowIfNull(url);
        if (store is not IMailboxStore mailboxStore)
        {
            throw new ArgumentException("Store must implement IMailboxStore.", nameof(store));
        }

        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // ---- TLS: certificate source (ACME store with hot reload, or a static PEM pair) ----
        CertificateWatcher? watcher = null;
        System.Security.Cryptography.X509Certificates.X509Certificate2? staticCert = null;
        if (tls?.Acme is { Configured: true } acmeEnv)
        {
            watcher = new CertificateWatcher(new CertificateStore(acmeEnv.Directory));
            builder.Services.AddSingleton(watcher);
        }
        else if (tls is not null && !string.IsNullOrEmpty(tls.StaticCertPath) && File.Exists(tls.StaticCertPath))
        {
            staticCert = !string.IsNullOrEmpty(tls.StaticKeyPath) && File.Exists(tls.StaticKeyPath)
                ? System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(tls.StaticCertPath, tls.StaticKeyPath)
                : System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(tls.StaticCertPath);
        }
        bool httpsOn = tls is { HttpsEnabled: true } && (watcher is not null || staticCert is not null);

        var listenUri = new Uri(url);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // A compose with attachments is the largest thing a browser sends.
            // The per-file check in /compose gives the user a message; this
            // hard ceiling stops anything bigger from being read at all.
            kestrel.Limits.MaxRequestBodySize = MaxRequestBytes;
            kestrel.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
            System.Net.IPAddress httpAddress = listenUri.Host == "localhost" ? System.Net.IPAddress.Loopback : System.Net.IPAddress.Parse(listenUri.Host);
            kestrel.Listen(httpAddress, listenUri.Port);
            if (httpsOn)
            {
                kestrel.Listen(httpAddress, tls!.HttpsPort, listen => listen.UseHttps(https =>
                {
                    // Consulted per connection: a renewed certificate is used by the next handshake.
                    https.ServerCertificateSelector = (_, _) => watcher?.Current ?? staticCert;
                }));
            }
        });

        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(mailboxStore);
        builder.Services.AddSingleton(maildir);
        builder.Services.AddSingleton(new MailboxService(mailboxStore, store, maildir, hostName));
        builder.Services.AddSingleton(new WebmailAuthService(mailboxStore));
        builder.Services.AddSingleton(new HostInfo(hostName));
        builder.Services.AddHttpContextAccessor();

        bool secure = string.Equals(Environment.GetEnvironmentVariable("ANJAL_WEBMAIL_SECURE"), "true", StringComparison.OrdinalIgnoreCase);
        builder.Services.AddAuthentication(CookieScheme).AddCookie(options =>
        {
            options.Cookie.Name = "anjal.session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = secure || httpsOn ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            options.LoginPath = "/sign-in";
            options.LogoutPath = "/sign-out";
            options.SlidingExpiration = true;
            options.ExpireTimeSpan = TimeSpan.FromHours(12);
        });
        builder.Services.AddAuthorization();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddRazorComponents();
        builder.Services.AddAntiforgery();
        builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(forms =>
        {
            forms.MultipartBodyLengthLimit = MaxRequestBytes;
            forms.ValueLengthLimit = 4 * 1024 * 1024;
        });
        builder.Services.AddSingleton(new LoginThrottle());
        builder.Services.AddSingleton(new AuditTrail(store));

        WebApplication app = builder.Build();

        // Any unhandled fault - a database outage, most likely - gets a page
        // that says so, with a reference that matches one line in the log
        // (DEF-040). Without this the webmail sent an empty 500 and the
        // reader saw only the browser's own "This page isn't working".
        app.Use(async (http, next) =>
        {
            try
            {
                await next().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Any fault becomes the same page; nothing about it is returned.
            catch (Exception ex)
            {
                string reference = Guid.NewGuid().ToString("N").Substring(0, 12);
                Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] 500 [{reference}] {http.Request.Method} {http.Request.Path}: {ex.GetType().Name}: {ex.Message}");
                if (!http.Response.HasStarted)
                {
                    http.Response.Clear();
                    http.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    http.Response.ContentType = "text/html; charset=utf-8";
                    ApplySecurityHeaders(http.Response.Headers);
                    await http.Response.WriteAsync(UnavailablePage(reference)).ConfigureAwait(false);
                }
            }
#pragma warning restore CA1031
        });

        // ACME HTTP-01 challenges are answered before anything else, on any listener.
        app.Use(async (http, next) =>
        {
            string? ka = Http01ChallengeStore.Lookup(http.Request.Path.Value ?? string.Empty);
            if (ka is not null)
            {
                http.Response.StatusCode = 200;
                http.Response.ContentType = "application/octet-stream";
                await http.Response.WriteAsync(ka).ConfigureAwait(false);
                return;
            }
            await next().ConfigureAwait(false);
        });

        if (httpsOn)
        {
            // Plain HTTP exists only for challenges: everything else goes to HTTPS,
            // but only once a certificate actually exists so a fresh install stays reachable.
            app.Use(async (http, next) =>
            {
                if (!http.Request.IsHttps && (watcher?.Current ?? staticCert) is not null)
                {
                    // The Host header is whatever the client sent; redirecting
                    // to it would make this an open redirect. Only a name this
                    // server answers to is used, otherwise the configured one.
                    http.Response.Redirect(
                        BuildHttpsRedirect(http.Request.Host.Host, hostName, tls!.Acme, tls.HttpsPort,
                            http.Request.PathBase.Value, http.Request.Path.Value, http.Request.QueryString.Value),
                        permanent: true);
                    return;
                }
                if (http.Request.IsHttps)
                {
                    http.Response.Headers.StrictTransportSecurity = "max-age=31536000; includeSubDomains";
                }
                await next().ConfigureAwait(false);
            });
        }

        // A form rendered before the session cookie was re-issued (a theme
        // change, a re-sign-in in another tab) carries an antiforgery token
        // that no longer validates. That is a stale page, not a fault:
        // send the reader to sign-in with an explanation instead of a 500.
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException)
            {
                if (!context.Response.HasStarted)
                {
                    context.Response.Clear();
                    context.Response.Redirect("/sign-in?expired=1");
                }
            }
        });

        app.Use(async (http, next) =>
        {
            ApplySecurityHeaders(http.Response.Headers);
            await next().ConfigureAwait(false);
        });

        app.UseAuthentication();

        // A form posted after the session has ended cannot be saved. Say so on
        // the sign-in page, rather than discarding the change silently
        // (DEF-012). The sign-in form itself is the one anonymous POST.
        app.Use(async (http, next) =>
        {
            if (HttpMethods.IsPost(http.Request.Method) &&
                http.User.Identity?.IsAuthenticated != true &&
                !http.Request.Path.StartsWithSegments("/auth/login", StringComparison.OrdinalIgnoreCase))
            {
                http.Response.Redirect("/sign-in?unsaved=1");
                return;
            }
            await next().ConfigureAwait(false);
        });
        app.UseAuthorization();
        app.UseAntiforgery();

        MapEndpoints(app);
        app.MapRazorComponents<App>();

        // Host the renewal service when configured to.
        if (tls?.Acme is { Host: true } hostEnv)
        {
            var renewal = new AcmeRenewalService(hostEnv.Options, new CertificateStore(hostEnv.Directory), line => Console.WriteLine(line));
            app.Lifetime.ApplicationStarted.Register(() => _ = renewal.RunAsync(app.Lifetime.ApplicationStopping));
        }
        return app;
    }

    private static void MapEndpoints(WebApplication app)
    {
        // ---- embedded static assets ----
        app.MapGet("/{file:regex(^(tokens\\.css|app\\.css|app\\.js)$)}", (string file, HttpContext http) => Asset(file, http));
        app.MapGet("/fonts/{file}", (string file, HttpContext http) => Asset("fonts/" + file, http));
        app.MapGet("/logos/{file}", (string file, HttpContext http) => Asset("logos/" + file, http));

        // ---- session ----
        app.MapPost("/auth/login", async (HttpContext http, [FromForm] string address, [FromForm] string password, WebmailAuthService auth, LoginThrottle throttle, AuditTrail audit, CancellationToken ct) =>
        {
            string client = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            string account = address.Trim().ToLowerInvariant();
            if (!throttle.IsAllowed(client, account))
            {
                // Refused before the password is checked: guessing costs the
                // attacker the wait, and costs this server nothing.
                await audit.RecordAsync("anonymous", "webmail.signin.throttled", account, client).ConfigureAwait(false);
                return Results.Redirect("/sign-in?throttled=1");
            }
            ClaimsPrincipal? principal = await auth.AuthenticateAsync(address.Trim(), password, ct).ConfigureAwait(false);
            if (principal is null)
            {
                throttle.RecordFailure(client, account);
                await audit.RecordAsync("anonymous", "webmail.signin.failed", account, client).ConfigureAwait(false);
                return Results.Redirect("/sign-in?error=1");
            }
            throttle.RecordSuccess(client, account);
            await audit.RecordAsync(account, "webmail.signin", account, client).ConfigureAwait(false);
            await http.SignInAsync(CookieScheme, principal, new AuthenticationProperties { IsPersistent = false }).ConfigureAwait(false);
            return Results.Redirect("/folder/INBOX");
        });

        app.MapPost("/sign-out", async (HttpContext http, IAntiforgery antiforgery, AuditTrail audit) =>
        {
            await antiforgery.ValidateRequestAsync(http).ConfigureAwait(false);
            string who = http.User.Identity?.Name ?? string.Empty;
            if (who.Length > 0)
            {
                await audit.RecordAsync(who, "webmail.signout", who, http.Connection.RemoteIpAddress?.ToString() ?? string.Empty).ConfigureAwait(false);
            }
            await http.SignOutAsync(CookieScheme).ConfigureAwait(false);
            return Results.Redirect("/sign-in");
        });

        // ---- one message ----
        app.MapPost("/message/{id:guid}/flag", async (HttpContext http, Guid id, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            MessageRow? row = await svc.GetOwnedRowAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            if (row is null)
            {
                return Results.NotFound();
            }
            await svc.SetFlagsAsync(mailboxId.Value, id, row.Seen, !row.Flagged, row.Answered, ct).ConfigureAwait(false);
            return Results.Redirect(SafeBack(back, $"/message/{id}"));
        }).RequireAuthorization();

        app.MapPost("/message/{id:guid}/read", async (HttpContext http, Guid id, [FromForm] string seen, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            MessageRow? row = await svc.GetOwnedRowAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            if (row is null)
            {
                return Results.NotFound();
            }
            await svc.SetFlagsAsync(mailboxId.Value, id, seen == "1", row.Flagged, row.Answered, ct).ConfigureAwait(false);
            return Results.Redirect(SafeBack(back, $"/message/{id}"));
        }).RequireAuthorization();

        app.MapPost("/message/{id:guid}/move", async (HttpContext http, Guid id, [FromForm] string folder, [FromForm] string? allow, [FromForm] string? block, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            MessageRow? moved = allow == "1"
                ? await svc.MarkNotSpamAsync(mailboxId.Value, id, ct).ConfigureAwait(false)
                : block == "1"
                    ? await svc.ReportSpamAsync(mailboxId.Value, id, ct).ConfigureAwait(false)
                    : await svc.MoveAsync(mailboxId.Value, id, folder, ct).ConfigureAwait(false);
            return moved is null ? Results.NotFound() : Results.Redirect(SafeBack(back, "/folder/INBOX"));
        }).RequireAuthorization();

        app.MapPost("/message/{id:guid}/delete", async (HttpContext http, Guid id, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            bool removed = await svc.DeleteAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            return removed ? Results.Redirect(SafeBack(back, "/folder/Trash")) : Results.NotFound();
        }).RequireAuthorization();

        app.MapPost("/message/{id:guid}/images", (HttpContext http, Guid id) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            return mailboxId is null ? Results.Redirect("/sign-in") : Results.Redirect($"/message/{id}?images=1");
        }).RequireAuthorization();

        app.MapGet("/message/{id:guid}/attachment/{index:int}", async (HttpContext http, Guid id, int index, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            (AttachmentView View, byte[] Bytes)? found = await svc.GetAttachmentAsync(mailboxId.Value, id, index, ct).ConfigureAwait(false);
            if (found is null)
            {
                return Results.NotFound();
            }
            // Always a download with a generic type: an HTML attachment must never
            // execute in the webmail's own origin.
            return Results.File(found.Value.Bytes, "application/octet-stream", found.Value.View.FileName);
        }).RequireAuthorization();

        app.MapGet("/message/{id:guid}/raw.eml", async (HttpContext http, Guid id, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            (MessageRow Row, FolderRow Folder, byte[] Raw)? found = await svc.ReadRawAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            return found is null ? Results.NotFound() : Results.File(found.Value.Raw, "message/rfc822", $"{id}.eml");
        }).RequireAuthorization();

        // ---- a folder full of messages ----
        app.MapPost("/folder/{name}/bulk", async (HttpContext http, string name, [FromForm] string action, [FromForm] Guid[] id, [FromForm] int? page, [FromForm] string? categoryId, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            MailboxService.BulkAction? bulk = action switch
            {
                "trash" => MailboxService.BulkAction.Trash,
                "read" => MailboxService.BulkAction.MarkRead,
                "unread" => MailboxService.BulkAction.MarkUnread,
                "spam" => MailboxService.BulkAction.ReportSpam,
                "notspam" => MailboxService.BulkAction.NotSpam,
                "restore" => MailboxService.BulkAction.Restore,
                "purge" => MailboxService.BulkAction.Purge,
                _ => null,
            };
            if (bulk is not null && id.Length > 0)
            {
                await svc.BulkAsync(mailboxId.Value, id, bulk.Value, ct).ConfigureAwait(false);
            }
            else if (action == "categorise" && id.Length > 0)
            {
                Guid? target = Guid.TryParse(categoryId, out Guid parsed) ? parsed : null;
                if (target is not null || categoryId == "clear")
                {
                    foreach (Guid messageId in id)
                    {
                        await svc.CategoriseAsync(mailboxId.Value, messageId, target, alsoFutureMail: false, ct).ConfigureAwait(false);
                    }
                }
            }
            return Results.Redirect($"/folder/{Uri.EscapeDataString(name)}?page={System.Math.Max(0, page ?? 0)}");
        }).RequireAuthorization();

        app.MapPost("/folder/{name}/readall", async (HttpContext http, string name, MailboxService svc, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            // This endpoint binds no form fields, so the framework's automatic
            // antiforgery check does not apply; without this line another site
            // could mark a whole folder read on the user's behalf (DEF-027).
            await antiforgery.ValidateRequestAsync(http).ConfigureAwait(false);
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            FolderRow? folder = await svc.GetFolderAsync(mailboxId.Value, name, ct).ConfigureAwait(false);
            if (folder is null)
            {
                return Results.NotFound();
            }
            int changed = await svc.MarkFolderReadAsync(mailboxId.Value, folder.Id, ct).ConfigureAwait(false);
            // The enhancement posts in the background and wants a small JSON reply;
            // a plain form post wants the page back.
            if (http.Request.Headers["X-Requested-With"] == "anjal")
            {
                return Results.Json(new { changed });
            }
            return Results.Redirect($"/folder/{Uri.EscapeDataString(name)}");
        }).RequireAuthorization();

        app.MapPost("/folder/Trash/empty", async (HttpContext http, MailboxService svc, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            // Permanent deletion: the token is checked explicitly, never left
            // to form binding (DEF-023).
            await antiforgery.ValidateRequestAsync(http).ConfigureAwait(false);
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.EmptyTrashAsync(mailboxId.Value, ct).ConfigureAwait(false);
            return Results.Redirect("/folder/Trash");
        }).RequireAuthorization();

        // ---- compose, drafts ----
        app.MapPost("/compose", async (HttpContext http, [FromForm] string to, [FromForm] string? cc, [FromForm] string? bcc, [FromForm] string? subject, [FromForm] string? body, [FromForm] string? draftId, [FromForm] string? inReplyTo, IFormFileCollection attachments, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            ComposeRequest request;
            try
            {
                request = await BuildRequestAsync(to, cc, bcc, subject, body, draftId, inReplyTo, attachments, ct).ConfigureAwait(false);
            }
            catch (AttachmentsTooLargeException)
            {
                return Results.Redirect("/compose" + ComposeQuery(AttachmentsTooLargeException.UserMessage, new ComposeRequest
                {
                    To = to,
                    Cc = cc ?? string.Empty,
                    Subject = subject ?? string.Empty,
                    Body = body ?? string.Empty,
                }));
            }
            ReadCarry(http.Request.Form, request);
            string? carryError = await svc.AddCarriedAttachmentsAsync(mailboxId.Value, request, ct).ConfigureAwait(false);
            string? error = carryError ?? await svc.SendAsync(mailboxId.Value, request, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Results.Redirect("/compose" + ComposeQuery(error, request));
            }
            return Results.Redirect("/folder/Sent?sent=1");
        }).RequireAuthorization();

        app.MapPost("/draft", async (HttpContext http, [FromForm] string? to, [FromForm] string? cc, [FromForm] string? bcc, [FromForm] string? subject, [FromForm] string? body, [FromForm] string? draftId, [FromForm] string? inReplyTo, [FromForm] string? autosave, IFormFileCollection attachments, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            ComposeRequest request;
            try
            {
                request = await BuildRequestAsync(to ?? string.Empty, cc, bcc, subject, body, draftId, inReplyTo, attachments, ct).ConfigureAwait(false);
            }
            catch (AttachmentsTooLargeException)
            {
                return Results.Redirect("/compose?error=" + Uri.EscapeDataString(AttachmentsTooLargeException.UserMessage));
            }
            ReadCarry(http.Request.Form, request);
            string? carryError = await svc.AddCarriedAttachmentsAsync(mailboxId.Value, request, ct).ConfigureAwait(false);
            if (carryError is not null)
            {
                return Results.Redirect("/compose?error=" + Uri.EscapeDataString(carryError));
            }
            Guid? saved = await svc.SaveDraftAsync(mailboxId.Value, request, ct).ConfigureAwait(false);
            if (saved is null)
            {
                return Results.NotFound();
            }
            if (autosave == "1" || http.Request.Headers["X-Requested-With"] == "anjal")
            {
                return Results.Json(new { id = saved.Value.ToString() });
            }
            return Results.Redirect($"/draft/{saved.Value}?saved=1");
        }).RequireAuthorization();

        // ---- settings ----
        app.MapPost("/settings/name", async (HttpContext http, [FromForm] string? displayName, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string? error = await svc.SetDisplayNameAsync(mailboxId.Value, displayName ?? string.Empty, ct).ConfigureAwait(false);
            if (error is null)
            {
                string who = http.User.Identity?.Name ?? string.Empty;
                await http.RequestServices.GetRequiredService<AuditTrail>().RecordAsync(
                    who, "webmail.displayname.changed", who, http.Connection.RemoteIpAddress?.ToString() ?? string.Empty).ConfigureAwait(false);
            }
            return Results.Redirect(error is null ? "/settings/profile?saved=name" : "/settings/profile?error=" + Uri.EscapeDataString(error));
        }).RequireAuthorization();

        app.MapPost("/settings/theme", async (HttpContext http, [FromForm] string theme, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string? error = await svc.SetThemeAsync(mailboxId.Value, theme, ct).ConfigureAwait(false);
            if (error is null)
            {
                // Re-issue the cookie so the new theme is on the root element
                // of the very next page, with no extra query per request.
                await http.SignInAsync(CookieScheme, WebmailAuthService.WithTheme(http.User, theme.Trim().ToLowerInvariant())).ConfigureAwait(false);
            }
            return Results.Redirect(error is null ? "/settings/appearance?saved=theme" : "/settings/appearance?error=" + Uri.EscapeDataString(error));
        }).RequireAuthorization();

        app.MapPost("/settings/password", async (HttpContext http, [FromForm] string current, [FromForm] string next, [FromForm] string confirm, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string? error = await svc.ChangePasswordAsync(mailboxId.Value, current, next, confirm, ct).ConfigureAwait(false);
            string who = http.User.Identity?.Name ?? string.Empty;
            await http.RequestServices.GetRequiredService<AuditTrail>().RecordAsync(
                who, error is null ? "webmail.password.changed" : "webmail.password.change-refused", who,
                http.Connection.RemoteIpAddress?.ToString() ?? string.Empty).ConfigureAwait(false);
            return Results.Redirect(error is null ? "/settings/password?saved=password" : "/settings/password?error=" + Uri.EscapeDataString(error));
        }).RequireAuthorization();

        // ---- categories ----
        app.MapPost("/message/{id:guid}/category", async (HttpContext http, Guid id, [FromForm] string? categoryId, [FromForm] string? future, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            Guid? category = Guid.TryParse(categoryId, out Guid parsed) ? parsed : null;
            bool ok = await svc.CategoriseAsync(mailboxId.Value, id, category, future == "1", ct).ConfigureAwait(false);
            return ok ? Results.Redirect(SafeBack(back, $"/message/{id}")) : Results.NotFound();
        }).RequireAuthorization();

        app.MapPost("/settings/categories", async (HttpContext http, [FromForm] string? name, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string? error = await svc.AddCategoryAsync(mailboxId.Value, name ?? string.Empty, ct).ConfigureAwait(false);
            return Results.Redirect(error is null ? "/settings/categories?saved=category" : "/settings/categories?error=" + Uri.EscapeDataString(error));
        }).RequireAuthorization();

        app.MapPost("/settings/categories/delete", async (HttpContext http, [FromForm] Guid categoryId, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string? error = await svc.DeleteCategoryAsync(mailboxId.Value, categoryId, ct).ConfigureAwait(false);
            return Results.Redirect(error is null ? "/settings/categories?saved=categoryremoved" : "/settings/categories?error=" + Uri.EscapeDataString(error));
        }).RequireAuthorization();

        app.MapPost("/settings/categories/rules/delete", async (HttpContext http, [FromForm] Guid ruleId, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.DeleteCategoryRuleAsync(mailboxId.Value, ruleId, ct).ConfigureAwait(false);
            return Results.Redirect("/settings/senders?saved=ruleremoved");
        }).RequireAuthorization();

        app.MapPost("/settings/signature", async (HttpContext http, [FromForm] string? signatureHtml, [FromForm] string? signatureText, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string? error = await svc.SetSignatureAsync(mailboxId.Value, signatureHtml ?? string.Empty, signatureText ?? string.Empty, ct).ConfigureAwait(false);
            return Results.Redirect(error is null ? "/settings/signature?saved=signature" : "/settings/signature?error=" + Uri.EscapeDataString(error));
        }).RequireAuthorization();

        app.MapPost("/settings/senders/add", async (HttpContext http, [FromForm] string? pattern, [FromForm] string? rule, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string? error = await svc.AddSenderRuleAsync(mailboxId.Value, pattern ?? string.Empty, rule ?? string.Empty, ct).ConfigureAwait(false);
            return error is null
                ? Results.Redirect("/settings/senders?saved=senderadded")
                : Results.Redirect("/settings/senders?error=" + Uri.EscapeDataString(error));
        }).RequireAuthorization();

        app.MapPost("/settings/senders/delete", async (HttpContext http, [FromForm] Guid ruleId, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.DeleteSenderRuleAsync(mailboxId.Value, ruleId, ct).ConfigureAwait(false);
            return Results.Redirect("/settings/senders?saved=senderremoved");
        }).RequireAuthorization();

        // ---- enhancement endpoints ----
        app.MapGet("/api/contacts", async (HttpContext http, string? q, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Unauthorized();
            }
            IReadOnlyList<ContactSuggestion> found = await svc.SuggestContactsAsync(mailboxId.Value, q ?? string.Empty, 8, ct).ConfigureAwait(false);
            return Results.Json(found.Select(c => new { name = c.Name, address = c.Address }));
        }).RequireAuthorization();

        app.MapGet("/api/ping", () => Results.NoContent()).RequireAuthorization();
    }

    /// <summary>
    /// Serve an embedded asset with its content type and a cache policy:
    /// fonts and logos are immutable for a year, the stylesheets and the
    /// script for an hour with an ETag carrying the build version, so a
    /// redeploy invalidates them.
    /// </summary>
    private static IResult Asset(string relativePath, HttpContext http)
    {
        byte[]? bytes = StaticAssets.Load(relativePath);
        string? fingerprint = StaticAssets.Fingerprint(relativePath);
        if (bytes is null || fingerprint is null)
        {
            return Results.NotFound();
        }

        // Cached for good only when the address carries the current
        // fingerprint (a change gives a new address) or the file is a font
        // binary or logo, fixed per name. Everything else - including
        // fonts/lipi.css - is revalidated on each use against its fingerprint.
        bool versioned = string.Equals(http.Request.Query["v"].ToString(), fingerprint, StringComparison.Ordinal);
        bool fixedFile = relativePath.EndsWith(".woff2", StringComparison.Ordinal) || relativePath.StartsWith("logos/", StringComparison.Ordinal);
        return new AssetResult(bytes, StaticAssets.ContentType(relativePath), versioned || fixedFile, fingerprint);
    }

    /// <summary>Writes an embedded asset with caching headers and ETag revalidation.</summary>
    private sealed class AssetResult : IResult
    {
        private readonly byte[] bytes;
        private readonly string contentType;
        private readonly bool immutable;
        private readonly string fingerprint;

        public AssetResult(byte[] bytes, string contentType, bool immutable, string fingerprint)
        {
            this.bytes = bytes;
            this.contentType = contentType;
            this.immutable = immutable;
            this.fingerprint = fingerprint;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            string etag = '"' + this.fingerprint + '"';
            httpContext.Response.Headers.CacheControl = this.immutable
                ? "public, max-age=31536000, immutable"
                : "no-cache";
            httpContext.Response.Headers.ETag = etag;
            if (this.contentType.StartsWith("font/", StringComparison.Ordinal))
            {
                // The message frame is sandboxed without an origin, so its font
                // requests are cross-origin. Fonts are public and carry no data.
                httpContext.Response.Headers.AccessControlAllowOrigin = "*";
            }
            if (httpContext.Request.Headers.IfNoneMatch.Contains(etag))
            {
                httpContext.Response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }
            httpContext.Response.ContentType = this.contentType;
            httpContext.Response.ContentLength = this.bytes.Length;
            await httpContext.Response.Body.WriteAsync(this.bytes).ConfigureAwait(false);
        }
    }

    /// <summary>Read which attachments to carry from a forwarded original or the draft being edited.</summary>
    private static void ReadCarry(IFormCollection form, ComposeRequest request)
    {
        // The formatting editor's HTML, when JavaScript is on; sanitised before use.
        request.BodyHtml = form["bodyHtml"].ToString();
        if (Guid.TryParse(form["carryFrom"].ToString(), out Guid from))
        {
            request.CarryFrom = from;
            foreach (string? value in form["carry"])
            {
                if (int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index))
                {
                    request.CarryIndexes.Add(index);
                }
            }
        }
    }

    /// <summary>Build a compose request from the posted form, reading any attachments into memory.</summary>
    private static async Task<ComposeRequest> BuildRequestAsync(string to, string? cc, string? bcc, string? subject, string? body, string? draftId, string? inReplyTo, IFormFileCollection attachments, CancellationToken ct)
    {
        var request = new ComposeRequest
        {
            To = to,
            Cc = cc ?? string.Empty,
            Bcc = bcc ?? string.Empty,
            Subject = subject ?? string.Empty,
            Body = body ?? string.Empty,
            InReplyTo = inReplyTo ?? string.Empty,
        };
        if (Guid.TryParse(draftId, out Guid parsed))
        {
            request.DraftId = parsed;
        }
        // Refuse before reading a byte: the total the browser declared is
        // checked first, so an oversized upload never lands in memory.
        long declared = 0;
        foreach (IFormFile file in attachments)
        {
            declared += Math.Max(0, file.Length);
        }
        if (declared > MaxAttachmentBytes)
        {
            throw new AttachmentsTooLargeException();
        }

        foreach (IFormFile file in attachments)
        {
            if (file.Length <= 0)
            {
                continue;
            }
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct).ConfigureAwait(false);
            request.Attachments.Add((file.FileName, file.ContentType, ms.ToArray()));
        }
        return request;
    }

    /// <summary>Round-trip a failed compose back to the form with what was typed.</summary>
    private static string ComposeQuery(string error, ComposeRequest request) =>
        $"?error={Uri.EscapeDataString(error)}&to={Uri.EscapeDataString(request.To)}&cc={Uri.EscapeDataString(request.Cc)}" +
        $"&subject={Uri.EscapeDataString(request.Subject)}&body={Uri.EscapeDataString(request.Body)}";

    /// <summary>Only allow same-origin relative redirects from form "back" fields.</summary>
    /// <remarks>
    /// Browsers treat a backslash as a slash, so <c>/\evil.example</c> is as
    /// much a protocol-relative URL as <c>//evil.example</c>. Any backslash
    /// or control character in the value refuses it.
    /// </remarks>
    private static string SafeBack(string? back, string fallback)
    {
        if (string.IsNullOrEmpty(back) || back[0] != '/' || back.Length > 2048)
        {
            return fallback;
        }
        if (back.Length > 1 && (back[1] == '/' || back[1] == '\\'))
        {
            return fallback;
        }
        foreach (char c in back)
        {
            if (c == '\\' || char.IsControl(c))
            {
                return fallback;
            }
        }
        return back;
    }

    /// <summary>The largest request body accepted: a compose with attachments.</summary>
    /// <remarks>
    /// Well above the 18 MB attachment limit plus encoding overhead, so an
    /// over-size upload is read and answered with a clear message rather
    /// than having its connection cut part-way (DEF-010). The browser
    /// checks the size before uploading as well.
    /// </remarks>
    internal const long MaxRequestBytes = 64L * 1024 * 1024;

    /// <summary>Total attachment bytes one message may carry, before encoding.</summary>
    internal const long MaxAttachmentBytes = MailboxService.MaxAttachmentBytes;

    /// <summary>Whether a Host header names this server and is safe to redirect to.</summary>
    /// <param name="requested">The Host header value.</param>
    /// <param name="hostName">The configured host name.</param>
    /// <param name="acme">ACME settings, whose domains are also this server's names.</param>
    /// <summary>
    /// Where a plain HTTP request should be sent on HTTPS. The scheme, host
    /// and port are decided here and never taken from the request: the Host
    /// header is used only if this server answers to that name, otherwise the
    /// configured one. The path and query are appended afterwards, so
    /// whatever they contain they cannot change the destination - the address
    /// always begins "https://&lt;our host&gt;/".
    /// </summary>
    /// <param name="requestedHost">The Host header, which the client controls.</param>
    /// <param name="hostName">This server's configured name.</param>
    /// <param name="acme">ACME settings, whose names also belong to this server.</param>
    /// <param name="httpsPort">The HTTPS port.</param>
    /// <param name="pathBase">The request path base.</param>
    /// <param name="path">The request path.</param>
    /// <param name="query">The query string, including its leading '?'.</param>
    public static string BuildHttpsRedirect(string? requestedHost, string hostName, AcmeEnvironment? acme, int httpsPort, string? pathBase, string? path, string? query)
    {
        string host = IsOwnHost(requestedHost ?? string.Empty, hostName, acme) ? requestedHost! : hostName;
        string portPart = httpsPort == 443 ? string.Empty : ":" + httpsPort.ToString(CultureInfo.InvariantCulture);
        string tail = (pathBase ?? string.Empty) + (path ?? string.Empty);
        if (!tail.StartsWith('/'))
        {
            tail = "/" + tail;      // a path is always rooted: "//evil.example" must not read as a host
        }
        while (tail.StartsWith("//", StringComparison.Ordinal))
        {
            tail = tail.Substring(1);
        }
        return "https://" + host + portPart + tail + (query ?? string.Empty);
    }

    public static bool IsOwnHost(string requested, string hostName, AcmeEnvironment? acme)
    {
        ArgumentNullException.ThrowIfNull(hostName);
        if (string.IsNullOrEmpty(requested))
        {
            return false;
        }
        if (string.Equals(requested, hostName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(requested, "localhost", StringComparison.OrdinalIgnoreCase) ||
            requested == "127.0.0.1" || requested == "::1" || requested == "[::1]")
        {
            return true;
        }
        if (acme is not null)
        {
            foreach (string d in acme.Domains)
            {
                if (string.Equals(requested, d, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Headers every webmail response carries. The content security policy
    /// allows only this origin's own script, style and fonts. Images may be
    /// remote because the message frame inherits this policy and "Load
    /// images" must work there; the frame carries its own stricter policy,
    /// and remote images are rewritten away by the sanitizer until the
    /// reader asks for them.
    /// </summary>
    /// <summary>
    /// The page shown when the webmail cannot serve a request - it names no
    /// component, driver or path, only a reference that appears in the log.
    /// </summary>
    /// <param name="reference">The reference recorded in the log.</param>
    private static string UnavailablePage(string reference) =>
        "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
        "<title>Anjal is briefly unavailable</title>" +
        "<link rel=\"stylesheet\" href=\"/tokens.css?v=" + (StaticAssets.Fingerprint("tokens.css") ?? string.Empty) + "\">" +
        "<link rel=\"stylesheet\" href=\"/app.css?v=" + (StaticAssets.Fingerprint("app.css") ?? string.Empty) + "\">" +
        "</head><body class=\"errpage\"><main class=\"errcard\">" +
        "<h1>Anjal is briefly unavailable</h1>" +
        "<p>Your mail is safe and nothing you have sent has been lost. This is the mail service itself, not your connection or your sign-in.</p>" +
        "<p>Please try again in a minute.</p>" +
        "<p class=\"errref\">If it keeps happening, quote this reference: <b>" + System.Net.WebUtility.HtmlEncode(reference) + "</b></p>" +
        "<p><a class=\"btn btn-p\" href=\"/folder/INBOX\">Try again</a></p>" +
        "</main></body></html>";

    internal static void ApplySecurityHeaders(IHeaderDictionary headers)
    {
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers.ContentSecurityPolicy =
            "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
            "img-src 'self' data: https:; font-src 'self'; connect-src 'self'; " +
            "frame-src 'self'; child-src 'self'; object-src 'none'; base-uri 'self'; " +
            "form-action 'self'; frame-ancestors 'none'";
    }

    /// <summary>The attachments on one message add up to more than <see cref="MaxAttachmentBytes"/>.</summary>
    private sealed class AttachmentsTooLargeException : Exception
    {
        public const string UserMessage = "The attachments add up to more than 18 MB. Remove some, or send them in more than one message.";
    }
}
