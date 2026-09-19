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

        bool secure = string.Equals(Environment.GetEnvironmentVariable("ANJAL_WEBMAIL_SECURE"), "true", StringComparison.OrdinalIgnoreCase);
        builder.Services.AddAuthentication(CookieScheme).AddCookie(options =>
        {
            options.Cookie.Name = "anjal.session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = secure || httpsOn ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            options.LoginPath = "/login";
            options.LogoutPath = "/auth/logout";
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

    private static readonly byte[] StyleSheet = LoadEmbedded("Anjal.Webmail.app.css");

    private static byte[] LoadEmbedded(string name)
    {
        using Stream? stream = typeof(Program).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' is missing.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/app.css", () => Results.Bytes(StyleSheet, "text/css; charset=utf-8"));

        app.MapPost("/auth/login", async (HttpContext http, [FromForm] string address, [FromForm] string password, WebmailAuthService auth, CancellationToken ct) =>
        {
            ClaimsPrincipal? principal = await auth.AuthenticateAsync(address.Trim(), password, ct).ConfigureAwait(false);
            if (principal is null)
            {
                return Results.Redirect("/login?error=1");
            }
            await http.SignInAsync(CookieScheme, principal, new AuthenticationProperties { IsPersistent = false }).ConfigureAwait(false);
            return Results.Redirect("/");
        });

        app.MapPost("/auth/logout", async (HttpContext http, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(http).ConfigureAwait(false);
            await http.SignOutAsync(CookieScheme).ConfigureAwait(false);
            return Results.Redirect("/login");
        });

        app.MapPost("/message/{id:guid}/flags", async (HttpContext http, Guid id, [FromForm] string seen, [FromForm] string flagged, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/login");
            }
            MessageRow? row = await svc.GetOwnedRowAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            if (row is null)
            {
                return Results.NotFound();
            }
            await svc.SetFlagsAsync(mailboxId.Value, id, seen == "1", flagged == "1", row.Answered, ct).ConfigureAwait(false);
            return Results.Redirect(SafeBack(back, $"/m/{id}"));
        }).RequireAuthorization();

        app.MapPost("/message/{id:guid}/move", async (HttpContext http, Guid id, [FromForm] string folder, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/login");
            }
            MessageRow? moved = await svc.MoveAsync(mailboxId.Value, id, folder, ct).ConfigureAwait(false);
            return moved is null ? Results.NotFound() : Results.Redirect(SafeBack(back, "/"));
        }).RequireAuthorization();

        app.MapPost("/message/{id:guid}/spam", async (HttpContext http, Guid id, [FromForm] string verdict, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/login");
            }
            MessageRow? moved = verdict == "ham"
                ? await svc.MarkNotSpamAsync(mailboxId.Value, id, ct).ConfigureAwait(false)
                : await svc.ReportSpamAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            return moved is null ? Results.NotFound() : Results.Redirect(SafeBack(back, "/"));
        }).RequireAuthorization();

        app.MapPost("/message/{id:guid}/delete", async (HttpContext http, Guid id, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/login");
            }
            bool removed = await svc.DeleteAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            return removed ? Results.Redirect(SafeBack(back, "/folder/Trash")) : Results.NotFound();
        }).RequireAuthorization();

        app.MapGet("/attachment/{id:guid}/{index:int}", async (HttpContext http, Guid id, int index, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/login");
            }
            (AttachmentView View, byte[] Bytes)? found = await svc.GetAttachmentAsync(mailboxId.Value, id, index, ct).ConfigureAwait(false);
            if (found is null)
            {
                return Results.NotFound();
            }
            // Serve as a download with a generic type so an HTML attachment
            // can never execute in the webmail origin.
            return Results.File(found.Value.Bytes, "application/octet-stream", found.Value.View.FileName);
        }).RequireAuthorization();

        app.MapGet("/raw/{id:guid}", async (HttpContext http, Guid id, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/login");
            }
            (MessageRow Row, FolderRow Folder, byte[] Raw)? found = await svc.ReadRawAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            return found is null ? Results.NotFound() : Results.File(found.Value.Raw, "message/rfc822", $"{id}.eml");
        }).RequireAuthorization();

        app.MapPost("/compose", async (HttpContext http, [FromForm] string to, [FromForm] string? cc, [FromForm] string? subject, [FromForm] string? body, IFormFileCollection attachments, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/login");
            }
            var request = new ComposeRequest
            {
                To = to,
                Cc = cc ?? string.Empty,
                Subject = subject ?? string.Empty,
                Body = body ?? string.Empty,
            };
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
            string? error = await svc.SendAsync(mailboxId.Value, request, ct).ConfigureAwait(false);
            if (error is not null)
            {
                string q = $"?error={Uri.EscapeDataString(error)}&to={Uri.EscapeDataString(to)}&cc={Uri.EscapeDataString(cc ?? string.Empty)}&subject={Uri.EscapeDataString(subject ?? string.Empty)}";
                return Results.Redirect("/compose" + q);
            }
            return Results.Redirect("/folder/Sent?sent=1");
        }).RequireAuthorization();
    }

    /// <summary>Only allow same-origin relative redirects from form "back" fields.</summary>
    private static string SafeBack(string? back, string fallback) =>
        !string.IsNullOrEmpty(back) && back.StartsWith('/') && !back.StartsWith("//", StringComparison.Ordinal) ? back : fallback;
}
