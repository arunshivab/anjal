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

        WebApplication app = builder.Build();

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
                    string host = http.Request.Host.Host;
                    string portPart = tls!.HttpsPort == 443 ? string.Empty : ":" + tls.HttpsPort.ToString(CultureInfo.InvariantCulture);
                    http.Response.Redirect("https://" + host + portPart + http.Request.PathBase + http.Request.Path + http.Request.QueryString, permanent: true);
                    return;
                }
                if (http.Request.IsHttps)
                {
                    http.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
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

        app.UseAuthentication();
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
        app.MapGet("/{file:regex(^(tokens\\.css|app\\.css|app\\.js)$)}", (string file) => Asset(file));
        app.MapGet("/fonts/{file}", (string file) => Asset("fonts/" + file));
        app.MapGet("/logos/{file}", (string file) => Asset("logos/" + file));

        // ---- session ----
        app.MapPost("/auth/login", async (HttpContext http, [FromForm] string address, [FromForm] string password, WebmailAuthService auth, CancellationToken ct) =>
        {
            ClaimsPrincipal? principal = await auth.AuthenticateAsync(address.Trim(), password, ct).ConfigureAwait(false);
            if (principal is null)
            {
                return Results.Redirect("/sign-in?error=1");
            }
            await http.SignInAsync(CookieScheme, principal, new AuthenticationProperties { IsPersistent = false }).ConfigureAwait(false);
            return Results.Redirect("/folder/INBOX");
        });

        app.MapPost("/sign-out", async (HttpContext http, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(http).ConfigureAwait(false);
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

        app.MapPost("/folder/{name}/readall", async (HttpContext http, string name, MailboxService svc, CancellationToken ct) =>
        {
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

        // ---- compose, drafts ----
        app.MapPost("/compose", async (HttpContext http, [FromForm] string to, [FromForm] string? cc, [FromForm] string? bcc, [FromForm] string? subject, [FromForm] string? body, [FromForm] string? draftId, [FromForm] string? inReplyTo, IFormFileCollection attachments, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            ComposeRequest request = await BuildRequestAsync(to, cc, bcc, subject, body, draftId, inReplyTo, attachments, ct).ConfigureAwait(false);
            string? error = await svc.SendAsync(mailboxId.Value, request, ct).ConfigureAwait(false);
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
            ComposeRequest request = await BuildRequestAsync(to ?? string.Empty, cc, bcc, subject, body, draftId, inReplyTo, attachments, ct).ConfigureAwait(false);
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
            return Results.Redirect(error is null ? "/settings?saved=name" : "/settings?error=" + Uri.EscapeDataString(error));
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
            return Results.Redirect(error is null ? "/settings?saved=theme" : "/settings?error=" + Uri.EscapeDataString(error));
        }).RequireAuthorization();

        app.MapPost("/settings/password", async (HttpContext http, [FromForm] string current, [FromForm] string next, [FromForm] string confirm, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string? error = await svc.ChangePasswordAsync(mailboxId.Value, current, next, confirm, ct).ConfigureAwait(false);
            return Results.Redirect(error is null ? "/settings?saved=password" : "/settings?error=" + Uri.EscapeDataString(error));
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
            return Results.Redirect(error is null ? "/settings?saved=category" : "/settings?error=" + Uri.EscapeDataString(error));
        }).RequireAuthorization();

        app.MapPost("/settings/categories/delete", async (HttpContext http, [FromForm] Guid categoryId, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string? error = await svc.DeleteCategoryAsync(mailboxId.Value, categoryId, ct).ConfigureAwait(false);
            return Results.Redirect(error is null ? "/settings?saved=categoryremoved" : "/settings?error=" + Uri.EscapeDataString(error));
        }).RequireAuthorization();

        app.MapPost("/settings/categories/rules/delete", async (HttpContext http, [FromForm] Guid ruleId, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.DeleteCategoryRuleAsync(mailboxId.Value, ruleId, ct).ConfigureAwait(false);
            return Results.Redirect("/settings?saved=ruleremoved");
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
    private static IResult Asset(string relativePath)
    {
        byte[]? bytes = StaticAssets.Load(relativePath);
        if (bytes is null)
        {
            return Results.NotFound();
        }
        return new AssetResult(bytes, StaticAssets.ContentType(relativePath), StaticAssets.IsImmutable(relativePath));
    }

    /// <summary>Writes an embedded asset with caching headers and ETag revalidation.</summary>
    private sealed class AssetResult : IResult
    {
        private readonly byte[] bytes;
        private readonly string contentType;
        private readonly bool immutable;

        public AssetResult(byte[] bytes, string contentType, bool immutable)
        {
            this.bytes = bytes;
            this.contentType = contentType;
            this.immutable = immutable;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            string etag = '"' + StaticAssets.Version + '"';
            httpContext.Response.Headers.CacheControl = this.immutable
                ? "public, max-age=31536000, immutable"
                : "public, max-age=3600";
            httpContext.Response.Headers.ETag = etag;
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
    private static string SafeBack(string? back, string fallback) =>
        !string.IsNullOrEmpty(back) && back.StartsWith('/') && !back.StartsWith("//", StringComparison.Ordinal) ? back : fallback;
}
