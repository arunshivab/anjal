using System.Globalization;
using Anjal.Acme;
using Anjal.Mailbox;
using Anjal.Store;
using Anjal.Webmail.Components;
using Anjal.Webmail.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
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

        // Settings live in the database (v1.0.0-rc.7): apply them before any
        // setting is read, so everything below sees the stored values.
        if (Environment.GetEnvironmentVariable("ANJAL_POSTGRES") is { Length: > 0 } settingsDb)
        {
            await StoredSettings.ApplyAsync(new PostgresMessageStore(settingsDb), SettingRow.WebmailScope, line => Console.WriteLine(line)).ConfigureAwait(false);
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
            MtaSts = MtaStsPolicy.FromEnvironment(hostname, out string mtaStsNote),
        };
        Console.WriteLine(mtaStsNote);

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
    // Set once the evidence line has been written (v1.0.0-rc.10).
    private static int evidenceNoteWritten;

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

        /// <summary>The MTA-STS policy served over HTTPS (v1.0.0-rc.10), or null when off.</summary>
        public MtaStsPolicy? MtaSts { get; init; }
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
            if (OperatingSystem.IsWindows() && staticCert.HasPrivateKey)
            {
                // A key loaded from PEM is ephemeral, and Windows' TLS (SChannel)
                // refuses ephemeral keys on a server: every handshake was closed
                // (found by the rc.7 HTTP/3 test on Windows, 27 Sep 2026). A
                // PKCS#12 round trip gives it a key it can use. Linux is unaffected.
                System.Security.Cryptography.X509Certificates.X509Certificate2 pem = staticCert;
                staticCert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
                    pem.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12), null);
                pem.Dispose();
            }
        }
        bool httpsOn = tls is { HttpsEnabled: true } && (watcher is not null || staticCert is not null);

        var listenUri = new Uri(url);
        string http3Note = string.Empty;
        bool http3 = httpsOn && Http3Available(out http3Note);
        if (httpsOn)
        {
            Console.WriteLine(http3
                ? $"HTTP/3: on (QUIC on UDP port {tls!.HttpsPort}; browsers learn of it through Alt-Svc and fall back to HTTP/2 where UDP is blocked)."
                : $"HTTP/3: off - {http3Note}; HTTP/1.1 and HTTP/2 only.");
        }
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
                // v1.0.0-rc.9.2 (DEF-084): an explicit cipher policy - TLS 1.3, or
                // TLS 1.2 with ECDHE and GCM or ChaCha20; no CBC suites. Kestrel
                // refuses the OnAuthenticate callback on a listener that also
                // serves HTTP/3, so TCP (HTTP/1.1 and HTTP/2) and UDP (HTTP/3)
                // get their own listeners on the same port. QUIC is TLS 1.3
                // only: no CBC suite can arise there.
                System.Net.Security.CipherSuitesPolicy? cipherPolicy = Anjal.Smtp.TlsCipherSet.WebmailPolicy();
                kestrel.Listen(httpAddress, tls!.HttpsPort, listen =>
                {
                    listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2;
                    listen.UseHttps(https =>
                    {
                        // Consulted per connection: a renewed certificate is used by the next handshake.
                        https.ServerCertificateSelector = (_, _) => watcher?.Current ?? staticCert;
                        if (cipherPolicy is not null)
                        {
                            https.OnAuthenticate = (_, ssl) => ssl.CipherSuitesPolicy = cipherPolicy;
                        }
                    });
                });
                if (http3)
                {
                    // HTTP/3 (v1.0.0-rc.7): QUIC sets up the connection and its
                    // encryption in one round trip and a lost packet delays only
                    // its own request - what slow, lossy routes need. On its own
                    // listener Kestrel no longer adds Alt-Svc; the pipeline does.
                    kestrel.Listen(httpAddress, tls.HttpsPort, listen =>
                    {
                        listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http3;
                        listen.UseHttps(https => https.ServerCertificateSelector = (_, _) => watcher?.Current ?? staticCert);
                    });
                }
            }
        });

        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(mailboxStore);
        builder.Services.AddSingleton(maildir);
        // v1.0.0-rc.9 (DEF-076): originals of mail sent from the webmail are kept when
        // the evidence folder exists (install.sh creates it; the unit may write there).
        string evidenceRoot = Environment.GetEnvironmentVariable("ANJAL_EVIDENCE_ROOT") ?? Anjal.Mailbox.EvidenceVault.DefaultRoot;
        Anjal.Mailbox.EvidenceRecorder? evidence = store is IEvidenceStore evidenceStore && Directory.Exists(evidenceRoot)
            ? new Anjal.Mailbox.EvidenceRecorder(evidenceStore, new Anjal.Mailbox.EvidenceVault(evidenceRoot))
            : null;
        // v1.0.0-rc.10 (D-75): once per process - tests build the app many times.
        // rc.15 (item 35): passwords set here are checked against the full leaked-password list,
        // which the server downloads and refreshes; the webmail picks up each new list by itself.
        bool firstBuild = System.Threading.Interlocked.CompareExchange(ref evidenceNoteWritten, 0, 0) == 0;
        Anjal.Smtp.PwnedPasswords.Use(Anjal.Smtp.PwnedPasswords.ConfiguredPath, firstBuild ? Console.WriteLine : null);
        if (System.Threading.Interlocked.Exchange(ref evidenceNoteWritten, 1) == 0)
        {
            Console.WriteLine(evidence is null
                ? $"Evidence: not kept for webmail sends (no folder at {evidenceRoot})."
                : $"Evidence: originals of webmail sends kept in {evidenceRoot}; a message is not sent unless its original is kept.");
        }
        builder.Services.AddSingleton(sp => new MailboxService(mailboxStore, store, maildir, hostName)
        {
            Evidence = evidence,
            // rc.13: authenticator secrets are kept encrypted with the webmail's own keys.
            Protector = sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("anjal.secrets.v1"),
        });
        builder.Services.AddSingleton(sp => new SessionRegistry(sp.GetRequiredService<MailboxService>()));
        builder.Services.AddSingleton(new WebmailAuthService(mailboxStore));
        builder.Services.AddHostedService(sp => new ScheduledSender(sp.GetRequiredService<MailboxService>(), OpsEndpoints.RootOf(sp.GetRequiredService<IMaildirStore>())));
        builder.Services.AddSingleton(new HostInfo(hostName));
        builder.Services.AddSingleton(Words.Load());
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
            // rc.13: how long a sign-in lasts is the session's business - 12 hours
            // on a shared computer, 30 days on the person's own, each ended
            // sooner when idle - so the cookie itself does not slide.
            options.SlidingExpiration = false;
            options.ExpireTimeSpan = SessionRegistry.OwnLife;
            options.Events = new CookieAuthenticationEvents
            {
                OnValidatePrincipal = async context =>
                {
                    SessionRegistry sessions = context.HttpContext.RequestServices.GetRequiredService<SessionRegistry>();
                    SessionState state = await sessions.CheckAsync(context.Principal!, AuthEndpoints.IsActivity(context.HttpContext.Request), context.HttpContext.RequestAborted).ConfigureAwait(false);
                    if (state != SessionState.Valid)
                    {
                        context.HttpContext.Items[AuthEndpoints.EndedItem] = state;
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(CookieScheme).ConfigureAwait(false);
                    }
                },
                OnRedirectToLogin = context =>
                {
                    string target = context.HttpContext.Items[AuthEndpoints.EndedItem] switch
                    {
                        SessionState.Idle => AuthEndpoints.SignedOutUrl(true, ZonedClock.For(null, null).ZoneId),
                        SessionState.Ended => "/sign-in?ended=1",
                        _ => context.RedirectUri,
                    };
                    context.Response.Redirect(target);
                    return Task.CompletedTask;
                },
            };
        });
        builder.Services.AddAuthorization();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddRazorComponents();
        builder.Services.AddAntiforgery();
        ConfigureDataProtection(builder.Services, Environment.GetEnvironmentVariable("ANJAL_WEBMAIL_KEYS_DIR"));
        builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(forms =>
        {
            forms.MultipartBodyLengthLimit = MaxRequestBytes;
            forms.ValueLengthLimit = 4 * 1024 * 1024;
        });
        builder.Services.AddSingleton(new LoginThrottle());
        builder.Services.AddSingleton(new AuditTrail(store));

        WebApplication app = builder.Build();

        // HEAD is answered wherever GET is (v1.0.0-rc.7): before, every HEAD got
        // 405. Kestrel keeps the headers and sends no body. This must run
        // before routing, so routing is placed explicitly right after it;
        // everything below keeps its order relative to routing.
        app.Use(async (http, next) =>
        {
            if (HttpMethods.IsHead(http.Request.Method))
            {
                http.Request.Method = HttpMethods.Get;
            }
            await next(http).ConfigureAwait(false);
        });
        app.UseRouting();

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
                // rc.15: where it happened (the first frame), so the reference leads somewhere; never the request's data.
                string where = (ex.StackTrace ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
                Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] 500 [{reference}] {http.Request.Method} {http.Request.Path}: {ex.GetType().Name}: {ex.Message} {where}");
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
                    if (http3 && !HttpProtocol.IsHttp3(http.Request.Protocol))
                    {
                        // Tells browsers HTTP/3 is on this port (rc.9.2: HTTP/3 has its own listener).
                        http.Response.Headers.AltSvc = $"h3=\":{tls!.HttpsPort}\"; ma=86400";
                    }
                }
                await next().ConfigureAwait(false);
            });
        }

        // v1.0.0-rc.10 (D-63): the MTA-STS policy, over HTTPS, for mta-sts.<domain>
        // only, never redirected (RFC 8461); 404 everywhere else and when off.
        MtaStsPolicy? mtaSts = tls?.MtaSts;
        app.Use(async (http, next) =>
        {
            if (string.Equals(http.Request.Path.Value, MtaStsPolicy.PolicyPath, StringComparison.Ordinal))
            {
                if (mtaSts is not null && http.Request.IsHttps && mtaSts.Serves(http.Request.Host.Host))
                {
                    http.Response.ContentType = "text/plain";
                    await http.Response.WriteAsync(mtaSts.Text()).ConfigureAwait(false);
                    return;
                }
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next(http).ConfigureAwait(false);
        });

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

        // rc.13: "leaves nothing behind" - no page of a signed-in mailbox is kept
        // in the browser's cache (the fingerprinted assets are not mail, and keep theirs).
        app.Use(async (http, next) =>
        {
            if (http.User.Identity?.IsAuthenticated == true)
            {
                http.Response.OnStarting(() =>
                {
                    if (!http.Response.Headers.ContainsKey("Cache-Control"))
                    {
                        http.Response.Headers.CacheControl = "no-store";
                    }
                    return Task.CompletedTask;
                });
            }
            await next().ConfigureAwait(false);
        });

        // A form posted after the session has ended cannot be saved. Say so on
        // the sign-in page, rather than discarding the change silently
        // (DEF-012). The sign-in form itself is the one anonymous POST.
        app.Use(async (http, next) =>
        {
            if (HttpMethods.IsPost(http.Request.Method) &&
                http.User.Identity?.IsAuthenticated != true &&
                !http.Request.Path.StartsWithSegments("/auth", StringComparison.OrdinalIgnoreCase))
            {
                http.Response.Redirect("/sign-in?unsaved=1");
                return;
            }
            await next().ConfigureAwait(false);
        });
        // DES-11 D7: a colour the organisation's administrator gave this person reaches their next
        // page - the colour rides in the sign-in cookie, so it is re-issued once.
        app.Use(async (http, next) =>
        {
            if (HttpMethods.IsGet(http.Request.Method) && WebmailAuthService.PersonIdOf(http.User) is Guid person)
            {
                System.Security.Claims.ClaimsPrincipal user = http.User;
                if (MailboxService.TakeThemeRefresh(person, out string theme))
                {
                    user = WebmailAuthService.WithTheme(user, theme);
                }
                if (MailboxService.TakeLanguageRefresh(person, out string language))
                {
                    user = WebmailAuthService.WithLanguage(user, language);
                }
                if (!ReferenceEquals(user, http.User))
                {
                    await AuthEndpoints.ResignAsync(http, user).ConfigureAwait(false);
                    http.User = user;
                }
            }
            await next().ConfigureAwait(false);
        });
        // rc.14: a shared mailbox's rights, and what the organisation asks first.
        app.Use(AuthEndpoints.DemandsAsync);
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
        // Owner, 8 Oct 2026: every organisation's clock (24- or 12-hour) is known before the first page.
        app.Lifetime.ApplicationStarted.Register(() => _ = LoadClocksAsync(app));
        return app;
    }

    private static async Task LoadClocksAsync(WebApplication app)
    {
        try
        {
            await app.Services.GetRequiredService<MailboxService>().LoadClocksAsync(app.Lifetime.ApplicationStopping).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The 24-hour clock is used until each organisation's is read at sign-in.
        catch (Exception ex)
        {
            Console.WriteLine("Clocks: could not read the organisations' clocks at start (" + ex.Message + "); each is read at sign-in.");
        }
#pragma warning restore CA1031
    }

    private static void MapEndpoints(WebApplication app)
    {
        // ---- embedded static assets ----
        app.MapGet("/{file:regex(^(tokens\\.css|app\\.css|app\\.js|sw\\.js|offline\\.js)$)}", (string file, HttpContext http) => Asset(file, http));
        app.MapGet("/fonts/{file}", (string file, HttpContext http) => Asset("fonts/" + file, http));
        app.MapGet("/logos/{file}", (string file, HttpContext http) => Asset("logos/" + file, http));

        ContactEndpoints.Map(app);

        // ---- rc.15 (DES-11 F1, owner 10 Oct): rules and folders were written in rc.12 but never
        // connected, so saving a rule or creating a folder answered 400. ----
        RuleEndpoints.Map(app);

        // ---- session (rc.13: AuthEndpoints) ----
        AuthEndpoints.Map(app);

        // ---- rc.14: the organisation console and the Anjal console ----
        OrgEndpoints.Map(app);
        OpsEndpoints.Map(app);

        // ---- rc.15: offline mail (item 65 b) ----
        OfflineEndpoints.Map(app);

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
            if (allow != "1" && block != "1")
            {
                // UX-07: Delete, Archive and Move to from an open message can be undone too,
                // the same way as from the list.
                (int count, Guid? token) = await svc.MoveWithUndoAsync(mailboxId.Value, new[] { id }, folder, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                if (count == 0)
                {
                    // Nothing moved (already there, or not this person's): as before.
                    return await svc.MoveAsync(mailboxId.Value, id, folder, ct).ConfigureAwait(false) is null ? Results.NotFound() : Results.Redirect(SafeBack(back, "/folder/INBOX"));
                }
                string to = SafeBack(back, "/folder/INBOX");
                if (token is not null && to.StartsWith("/folder/", StringComparison.Ordinal))
                {
                    to += (to.Contains('?', StringComparison.Ordinal) ? "&" : "?") + $"undo={token}&moved={count}&to={Uri.EscapeDataString(folder)}";
                }
                return Results.Redirect(to);
            }
            MessageRow? moved = allow == "1"
                ? await svc.MarkNotSpamAsync(mailboxId.Value, id, ct).ConfigureAwait(false)
                : await svc.ReportSpamAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            return moved is null ? Results.NotFound() : Results.Redirect(SafeBack(back, "/folder/INBOX"));
        }).RequireAuthorization();

        app.MapPost("/message/{id:guid}/delete", async (HttpContext http, Guid id, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            // rc.14: nothing is deleted for good from a mailbox under a legal hold.
            if (await svc.IsHeldAsync(mailboxId.Value, ct).ConfigureAwait(false))
            {
                await svc.AddNoticeAsync(mailboxId.Value, "This mailbox is under a legal hold, so nothing can be deleted for good until the hold is lifted.", ct).ConfigureAwait(false);
                return Results.Redirect(SafeBack(back, "/folder/Trash"));
            }
            bool removed = await svc.DeleteAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            return removed ? Results.Redirect(SafeBack(back, "/folder/Trash")) : Results.NotFound();
        }).RequireAuthorization();

        // DEF-093 (found in rc.12 review): this and Block sender bind no form
        // fields, so they need the antiforgery check added explicitly.
        app.MapPost("/message/{id:guid}/trust-sender", async (HttpContext http, Guid id, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string? trustedAddress = await svc.TrustSenderOfAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            return trustedAddress is null ? Results.NotFound() : Results.Redirect($"/message/{id}");
        }).RequireAuthorization().AddEndpointFilter(RequireAntiforgery);

        app.MapPost("/message/{id:guid}/images", (HttpContext http, Guid id) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            return mailboxId is null ? Results.Redirect("/sign-in") : Results.Redirect($"/message/{id}?images=1");
        }).RequireAuthorization().AddEndpointFilter(RequireAntiforgery); // DES-11 F2, system-wide: every post is checked

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

        // rc.12 (UX-06, the board GapPreview): images and PDFs shown in the letter
        // area. Only raster images (never SVG) and PDF; everything else stays a
        // download. Nothing served here can run in the webmail's own page.
        app.MapGet("/message/{id:guid}/attachment/{index:int}/preview", async (HttpContext http, Guid id, int index, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            (AttachmentView View, byte[] Bytes)? found = await svc.GetAttachmentAsync(mailboxId.Value, id, index, ct).ConfigureAwait(false);
            string? type = found is null ? null : MailboxService.PreviewType(found.Value.View);
            if (found is null || type is null)
            {
                return Results.NotFound();
            }
            http.Response.Headers.ContentSecurityPolicy = type == "application/pdf"
                ? "default-src 'none'; frame-ancestors 'self'"
                : "default-src 'none'; img-src 'self'; sandbox; frame-ancestors 'self'";
            http.Response.Headers.XFrameOptions = "SAMEORIGIN";
            http.Response.Headers.ContentDisposition = "inline";
            return Results.Bytes(found.Value.Bytes, type);
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

        // rc.11 (D-103): block the sender - future mail to Junk; this message stays.
        app.MapPost("/message/{id:guid}/block", async (HttpContext http, Guid id, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string? blocked = await svc.BlockSenderAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            return Results.Redirect(blocked is null ? "/folder/INBOX" : $"/message/{id}?blocked={Uri.EscapeDataString(blocked)}");
        }).RequireAuthorization().AddEndpointFilter(RequireAntiforgery);

        // rc.11 (D-103): print - the letter with its envelope details. The body is
        // the same cleaned HTML the message page shows; everything else is
        // escaped; and this page's own policy allows no outside loading at all.
        app.MapGet("/message/{id:guid}/print", async (HttpContext http, Guid id, MailboxService svc, Words words, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            MessageView? view = await svc.OpenAsync(mailboxId.Value, id, allowRemoteImages: false, ct).ConfigureAwait(false);
            if (view is null)
            {
                return Results.NotFound();
            }
            Lexicon l = words.For(WebmailAuthService.LanguageOf(http.User, words));
            ZonedClock clock = WebmailAuthService.ClockOf(http.User);
            static string E(string s) => System.Net.WebUtility.HtmlEncode(s);
            var page = new System.Text.StringBuilder();
            page.Append("<!DOCTYPE html><html lang=\"").Append(E(l.Language)).Append("\"><head><meta charset=\"utf-8\">")
                .Append("<title>").Append(E(view.Subject)).Append(" - Anjal</title>")
                .Append("<link rel=\"stylesheet\" href=\"/fonts/lipi.css\">")
                .Append("<style>body{font-family:'LiPi Sans',system-ui,sans-serif;font-size:14px;line-height:1.5;margin:24px;color:#111;background:#fff}")
                .Append("h1{font-size:20px;margin:0 0 12px}table{border-collapse:collapse;margin:0 0 12px}th{text-align:left;padding:2px 12px 2px 0;color:#555;font-weight:600;vertical-align:top}")
                .Append("td{padding:2px 0}hr{border:0;border-top:1px solid #ccc;margin:12px 0}img{max-width:100%}pre{white-space:pre-wrap;font-family:inherit}</style>")
                .Append("</head><body data-autoprint><header><h1>").Append(E(string.IsNullOrWhiteSpace(view.Subject) ? l["(no subject)"] : view.Subject)).Append("</h1><table>");
            void Row(string label, string value)
            {
                if (value.Length > 0)
                {
                    page.Append("<tr><th>").Append(E(label)).Append("</th><td>").Append(E(value)).Append("</td></tr>");
                }
            }
            Row(l["From"], view.From);
            Row(l["To"], view.To);
            Row(l["Cc"], view.Cc);
            Row(l["Received"], clock.Full(view.Row.ReceivedAt));
            Row(l["Attachments"], string.Join(", ", view.Attachments.Select(a => a.FileName)));
            page.Append("</table></header><hr><main>").Append(view.BodyHtml).Append("</main><script src=\"/app.js\"></script></body></html>");
            http.Response.Headers.ContentSecurityPolicy =
                "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src data:; font-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
            return Results.Content(page.ToString(), "text/html; charset=utf-8");
        }).RequireAuthorization();

        // ---- a folder full of messages ----
        app.MapPost("/folder/{name}/bulk", async (HttpContext http, string name, [FromForm] string action, [FromForm] Guid[] id, [FromForm] int? page, [FromForm] string? categoryId, [FromForm] string? to, [FromForm] string? all, [FromForm] string? show, [FromForm] string? back, [FromQuery] Guid? one, MailboxService svc, CancellationToken ct) =>
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
            // rc.11 (item 13): "Select all in this folder" acts on every message the
            // filter shows, not only this page; at most 5,000 at a time.
            if (string.Equals(all, "1", StringComparison.Ordinal))
            {
                FolderView? whole = (await svc.ListFoldersAsync(mailboxId.Value, ct).ConfigureAwait(false)).FirstOrDefault(f => f.Name == name);
                if (whole is not null)
                {
                    // The list hands out at most 200 a page, so read page by page.
                    var everyone = new List<Guid>();
                    for (int p = 0; everyone.Count < 5000; p++)
                    {
                        IReadOnlyList<MessageRow> chunk = (await svc.ListFilteredAsync(mailboxId.Value, whole.Id, show, p, 200, ct).ConfigureAwait(false)).Items;
                        everyone.AddRange(chunk.Select(m => m.Id));
                        if (chunk.Count < 200)
                        {
                            break;
                        }
                    }
                    id = everyone.Take(5000).ToArray();
                }
            }

            // rc.12 (D-116): a row's own hover buttons act on that one message,
            // whatever else is ticked, through this same path (so Undo works).
            if (one is Guid single)
            {
                id = new[] { single };
            }

            // rc.11 (UX-07): Trash, Restore and Move to are moves that can be undone.
            string? moveTo = action switch
            {
                "trash" => "Trash",
                "restore" => FolderRow.Inbox,
                "move" => to,
                "archive" => "Archive",
                _ => null,
            };
            if (moveTo is not null && id.Length > 0)
            {
                (int moved, Guid? token) = await svc.MoveWithUndoAsync(mailboxId.Value, id, moveTo, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                string undo = token is null ? string.Empty : $"&undo={token}&moved={moved}&to={Uri.EscapeDataString(moveTo)}";
                // rc.15 (item 37): ticked in the search results, back to those results.
                if (FromSearch(back) is string there)
                {
                    return Results.Redirect(there + (there.Contains('?', StringComparison.Ordinal) ? "&" : "?") + undo.TrimStart('&'));
                }
                return Results.Redirect(FolderUrl(name, page, show) + undo);
            }
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
            return Results.Redirect(FromSearch(back) ?? FolderUrl(name, page, show));
        }).RequireAuthorization();

        app.MapPost("/folder/{name}/readall", async (HttpContext http, string name, [FromForm] string? show, MailboxService svc, IAntiforgery antiforgery, CancellationToken ct) =>
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
            return Results.Redirect(FolderUrl(name, 0, show));
        }).RequireAuthorization();

        // rc.12 (item 23): mark every message in a folder unread.
        app.MapPost("/folder/{name}/unreadall", async (HttpContext http, string name, [FromForm] string? show, MailboxService svc, CancellationToken ct) =>
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
            await svc.MarkFolderUnreadAsync(mailboxId.Value, folder.Id, ct).ConfigureAwait(false);
            return Results.Redirect(FolderUrl(name, 0, show));
        }).RequireAuthorization().AddEndpointFilter(RequireAntiforgery);

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

        app.MapPost("/folder/Junk/empty", async (HttpContext http, MailboxService svc, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            // SPEC-11 item 25: Empty Junk, checked like Empty Trash (DEF-023).
            await antiforgery.ValidateRequestAsync(http).ConfigureAwait(false);
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.EmptyJunkAsync(mailboxId.Value, ct).ConfigureAwait(false);
            return Results.Redirect("/folder/" + Anjal.Mailbox.MailboxSink.JunkFolder);
        }).RequireAuthorization();

        // ---- compose, drafts ----
        app.MapPost("/compose", async (HttpContext http, [FromForm] string to, [FromForm] string? cc, [FromForm] string? bcc, [FromForm] string? subject, [FromForm] string? body, [FromForm] string? draftId, [FromForm] string? inReplyTo, [FromForm] string? undo, [FromForm] string? sendAt, [FromForm] string? sendAtLocal, [FromForm] string? back, [FromForm] string? merge, [FromForm] string? expect, IFormFileCollection attachments, MailboxService svc, CancellationToken ct) =>
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
            // rc.14: from a shared mailbox set to send "on behalf", the person is named as the Sender.
            request.Sender = await svc.SenderForAsync(WebmailAuthService.PersonIdOf(http.User), mailboxId.Value, ct).ConfigureAwait(false);
            string? carryError = await svc.AddCarriedAttachmentsAsync(mailboxId.Value, request, ct).ConfigureAwait(false);
            if (carryError is not null)
            {
                return Results.Redirect("/compose" + ComposeQuery(carryError, request));
            }

            // rc.15 (item 63): "Expect a reply" - counted on the dashboard if no answer comes in three days.
            if (string.Equals(expect, "1", StringComparison.Ordinal))
            {
                await svc.MarkExpectReplyAsync(mailboxId.Value, request.To, request.Subject, ct).ConfigureAwait(false);
            }

            // rc.15 (item 29): the people written to join the writer's contacts (an uploaded list does not).
            if (WebmailAuthService.PersonIdOf(http.User) is Guid writer)
            {
                await svc.RememberRecipientsAsync(writer, http.User.Identity?.Name ?? string.Empty, request, ct).ConfigureAwait(false);
            }

            // rc.12 (item 64): Send one each - one copy per person, blanks filled, sent gradually.
            if (string.Equals(merge, "1", StringComparison.Ordinal))
            {
                // Owner, 8 Oct 2026: only for people their organisation has given it to.
                if (WebmailAuthService.PersonIdOf(http.User) is not Guid sender || !await svc.MayMergeAsync(sender, ct).ConfigureAwait(false))
                {
                    return Results.Redirect("/compose" + ComposeQuery(MailboxService.MergeNotAllowed, request));
                }
                string? list = null;
                IFormFile? listFile = http.Request.Form.Files.GetFile("mergeList");
                if (listFile is { Length: > 0 and <= 2 * 1024 * 1024 })
                {
                    using var reader = new StreamReader(listFile.OpenReadStream(), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    list = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                }
                DateTimeOffset now = DateTimeOffset.UtcNow;
                DateTimeOffset first = string.IsNullOrEmpty(sendAt) ? now : SendLaterTime(WebmailAuthService.ClockOf(http.User), now, sendAt, sendAtLocal) ?? now;
                // rc.15 (item 64): how the person matched each blank, and what to write when a value is missing.
                var map = new Dictionary<string, MergeBlank>(StringComparer.Ordinal);
                Microsoft.Extensions.Primitives.StringValues blanks = http.Request.Form["mapBlank"];
                Microsoft.Extensions.Primitives.StringValues fields = http.Request.Form["mapField"];
                Microsoft.Extensions.Primitives.StringValues fallbacks = http.Request.Form["mapFallback"];
                for (int i = 0; i < blanks.Count && i < 50; i++)
                {
                    string blank = blanks[i] ?? string.Empty;
                    if (blank.Length is > 0 and <= 40)
                    {
                        map[blank] = new MergeBlank(i < fields.Count ? (fields[i] ?? string.Empty).Trim() : string.Empty, i < fallbacks.Count ? (fallbacks[i] ?? string.Empty).Trim() : string.Empty);
                    }
                }
                request.MergeSummaryTo = http.Request.Form["mergeSummary"].ToString();
                (string? mergeError, int made) = await svc.SendOneEachAsync(mailboxId.Value, request, list, first, map.Count > 0 ? map : null, WebmailAuthService.ClockOf(http.User), ct).ConfigureAwait(false);
                return mergeError is null
                    ? Results.Redirect($"/folder/{MailboxService.ScheduledFolder}?merged={made}")
                    : Results.Redirect("/compose" + ComposeQuery(mergeError, request));
            }

            // rc.12 (item 8): Send later - kept in Scheduled until its time.
            if (!string.IsNullOrEmpty(sendAt))
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                DateTimeOffset? when = SendLaterTime(WebmailAuthService.ClockOf(http.User), now, sendAt, sendAtLocal);
                if (when is null || when.Value <= now.AddMinutes(1))
                {
                    return Results.Redirect("/compose" + ComposeQuery("Pick a send time in the future.", request));
                }
                (string? holdError, Guid? _) = await svc.HoldAsync(mailboxId.Value, request, when.Value, ct).ConfigureAwait(false);
                return holdError is null
                    ? Results.Redirect("/folder/" + MailboxService.ScheduledFolder + "?scheduled=1")
                    : Results.Redirect("/compose" + ComposeQuery(holdError, request));
            }

            // rc.12 (UX-02): with the script on, Send waits so it can be undone -
            // as long as the person chose in Mail settings (rc.13), or not at all.
            int wait = (await svc.GetMailSettingsAsync(mailboxId.Value, ct).ConfigureAwait(false)).UndoSeconds;
            if (string.Equals(undo, "1", StringComparison.Ordinal) && wait > 0)
            {
                (string? holdError, Guid? held) = await svc.HoldAsync(mailboxId.Value, request, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);
                if (holdError is not null || held is null)
                {
                    return Results.Redirect("/compose" + ComposeQuery(holdError ?? "Not sent.", request));
                }
                string after = SafeBack(back, "/folder/INBOX");
                return Results.Redirect(after + (after.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "held=" + held.Value + "&wait=" + wait.ToString(CultureInfo.InvariantCulture));
            }

            string? error = await svc.SendAsync(mailboxId.Value, request, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Results.Redirect("/compose" + ComposeQuery(error, request));
            }
            return Results.Redirect("/folder/Sent?sent=1");
        }).RequireAuthorization();

        app.MapPost("/draft", async (HttpContext http, [FromForm] string? to, [FromForm] string? cc, [FromForm] string? bcc, [FromForm] string? subject, [FromForm] string? body, [FromForm] string? draftId, [FromForm] string? inReplyTo, [FromForm] string? autosave, [FromForm] string? next, [FromForm] string? templates, [FromForm] string? savetemplate, [FromForm] string? expand, [FromForm] string? dock, [FromForm] string? back, IFormFileCollection attachments, MailboxService svc, CancellationToken ct) =>
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
            // rc.14: from a shared mailbox set to send "on behalf", the person is named as the Sender.
            request.Sender = await svc.SenderForAsync(WebmailAuthService.PersonIdOf(http.User), mailboxId.Value, ct).ConfigureAwait(false);
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
            // rc.12 (item 62): Templates opens the picker over this draft;
            // "Save as template" keeps the message as one of the person's own.
            if (templates == "1")
            {
                return Results.Redirect($"/draft/{saved.Value}?templates=1");
            }
            // rc.14: an administrator may save it for everyone in the organisation.
            if (savetemplate == "org")
            {
                string? orgError = WebmailAuthService.PersonIdOf(http.User) is Guid person && await svc.IsOrgAdminAsync(person, ct).ConfigureAwait(false)
                    && await svc.TenantOfAsync(person, ct).ConfigureAwait(false) is Guid tenantId
                    ? await svc.SaveOrgTemplateAsync(tenantId, subject ?? string.Empty, subject ?? string.Empty, body ?? string.Empty, ct).ConfigureAwait(false)
                    : "Only an administrator can save a template for everyone.";
                return Results.Redirect($"/draft/{saved.Value}?" + (orgError is null ? "templatesaved=1" : "error=" + Uri.EscapeDataString(orgError)));
            }
            if (savetemplate == "1")
            {
                string? templateError = await svc.SaveOwnTemplateAsync(mailboxId.Value, subject ?? string.Empty, subject ?? string.Empty, body ?? string.Empty, ct).ConfigureAwait(false);
                return Results.Redirect($"/draft/{saved.Value}?" + (templateError is null ? "templatesaved=1" : "error=" + Uri.EscapeDataString(templateError)));
            }
            // rc.12 (item 17): "Save draft" in Keep this draft? goes where the person was going.
            if (!string.IsNullOrEmpty(next))
            {
                string onward = SafeBack(next, "/folder/Drafts");
                return Results.Redirect(onward + (onward.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "saved=1");
            }
            // rc.15 (owner, 7 Oct; item 48): full view and the small window, each opening the other.
            if (expand == "1")
            {
                return Results.Redirect($"/draft/{saved.Value}");
            }
            if (dock == "1")
            {
                string list = SafeBack(back, "/folder/INBOX");
                if (!list.StartsWith("/folder/", StringComparison.Ordinal))
                {
                    list = "/folder/INBOX";
                }
                return Results.Redirect(list + (list.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "write=" + saved.Value);
            }
            return Results.Redirect($"/draft/{saved.Value}?saved=1");
        }).RequireAuthorization();

        // rc.12 (item 17): "Keep this draft?" - Discard removes the draft that
        // autosave or Save draft made, and only ever a message in Drafts.
        app.MapPost("/draft/discard", async (HttpContext http, [FromForm] string? draftId, [FromForm] string? next, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            if (Guid.TryParse(draftId, out Guid id))
            {
                await svc.DiscardDraftAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            }
            return Results.Redirect(SafeBack(string.IsNullOrEmpty(next) ? back : next, "/folder/INBOX"));
        }).RequireAuthorization();

        // rc.15 (item 63): "No reply needed" takes a sent message out of the dashboard's count.
        app.MapPost("/message/{id:guid}/noreply", async (HttpContext http, Guid id, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.MarkNoReplyNeededAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            return Results.Redirect(SafeBack(back, "/no-reply"));
        }).RequireAuthorization().AddEndpointFilter(RequireAntiforgery);

        // rc.12 (UX-04): mark every message of a conversation read.
        app.MapPost("/message/{id:guid}/conversation-read", async (HttpContext http, Guid id, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.MarkConversationReadAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            return Results.Redirect(SafeBack(back, $"/message/{id}"));
        }).RequireAuthorization();

        // rc.12 (item 62): use a template in a draft; remove one of your own.
        app.MapPost("/draft/{id:guid}/template", async (HttpContext http, Guid id, [FromForm] string? t, [FromForm] string? festival, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            MailTemplate? template = await svc.FindTemplateAsync(mailboxId.Value, t, ct).ConfigureAwait(false);
            ComposeRequest? draft = await svc.LoadDraftAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            if (template is null || draft is null)
            {
                return Results.Redirect($"/draft/{id}?templates=1");
            }
            var values = new Dictionary<string, string>(await svc.KnownBlanksAsync(mailboxId.Value, draft.To, festival, ct).ConfigureAwait(false), StringComparer.Ordinal);
            IFormCollection form = http.Request.Form;
            foreach (string blank in TemplateCatalogue.BlanksIn(template.Subject + "\n" + template.Body))
            {
                string typed = form["blank:" + blank].ToString().Trim();
                if (typed.Length > 0)
                {
                    values[blank] = typed.Length > 500 ? typed[..500] : typed;
                }
            }
            Guid? applied = await svc.ApplyTemplateAsync(mailboxId.Value, id, template, values, ct).ConfigureAwait(false);
            return Results.Redirect(applied is Guid a ? $"/draft/{a}" : $"/draft/{id}?templates=1");
        }).RequireAuthorization();

        app.MapPost("/templates/delete", async (HttpContext http, [FromForm] string? key, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.DeleteOwnTemplateAsync(mailboxId.Value, key ?? string.Empty, ct).ConfigureAwait(false);
            return Results.Redirect(SafeBack(back, "/compose"));
        }).RequireAuthorization();

        // rc.12 (items 8, 9, UX-02): mail waiting to be sent, and the Outbox.
        app.MapPost("/held/{id:guid}/undo", async (HttpContext http, Guid id, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            Guid? draft = await svc.UnholdAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            return Results.Redirect(draft is Guid d ? $"/draft/{d}?undone=1" : "/folder/Sent?toolate=1");
        }).RequireAuthorization().AddEndpointFilter(RequireAntiforgery);

        app.MapPost("/held/{id:guid}/edit", async (HttpContext http, Guid id, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            Guid? draft = await svc.UnholdAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            return Results.Redirect(draft is Guid d ? $"/draft/{d}" : "/folder/" + MailboxService.ScheduledFolder);
        }).RequireAuthorization().AddEndpointFilter(RequireAntiforgery);

        app.MapPost("/held/{id:guid}/cancel", async (HttpContext http, Guid id, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            Guid? draft = await svc.UnholdAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            return Results.Redirect("/folder/" + MailboxService.ScheduledFolder + (draft is null ? string.Empty : "?cancelled=1"));
        }).RequireAuthorization().AddEndpointFilter(RequireAntiforgery);

        app.MapPost("/held/{id:guid}/send", async (HttpContext http, Guid id, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string? error = await svc.SendHeldAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            return Results.Redirect(error is null ? "/folder/Sent?sent=1" : "/folder/Drafts");
        }).RequireAuthorization().AddEndpointFilter(RequireAntiforgery);

        app.MapPost("/held/{id:guid}/reschedule", async (HttpContext http, Guid id, [FromForm] string? sendAt, [FromForm] string? sendAtLocal, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset? when = SendLaterTime(WebmailAuthService.ClockOf(http.User), now, sendAt ?? string.Empty, sendAtLocal);
            if (when is null || when.Value <= now.AddMinutes(1))
            {
                return Results.Redirect($"/folder/{MailboxService.ScheduledFolder}?open={id}&badtime=1");
            }
            (string? error, Guid? moved) = await svc.RescheduleAsync(mailboxId.Value, id, when.Value, ct).ConfigureAwait(false);
            return Results.Redirect(error is null && moved is Guid m ? $"/folder/{MailboxService.ScheduledFolder}?open={m}" : "/folder/" + MailboxService.ScheduledFolder);
        }).RequireAuthorization();

        app.MapPost("/outbox/{id:guid}/cancel", async (HttpContext http, Guid id, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            bool done = await svc.CancelOutboxAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            return Results.Redirect(done ? "/outbox?cancelled=1" : "/outbox");
        }).RequireAuthorization().AddEndpointFilter(RequireAntiforgery);

        app.MapPost("/outbox/{id:guid}/retry", async (HttpContext http, Guid id, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.RetryOutboxAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            return Results.Redirect($"/outbox?open={id}&retrying=1");
        }).RequireAuthorization().AddEndpointFilter(RequireAntiforgery);

        // ---- settings ----
        app.MapPost("/settings/name", async (HttpContext http, [FromForm] string? displayName, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
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
            return Results.Redirect(error is null ? "/settings/mail?saved=name" : "/settings/mail?error=" + Uri.EscapeDataString(error));
        }).RequireAuthorization();

        app.MapPost("/settings/theme", async (HttpContext http, [FromForm] string theme, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string? error = await svc.SetThemeAsync(mailboxId.Value, theme, ct).ConfigureAwait(false);
            if (error is null)
            {
                // Re-issue the cookie so the new theme is on the root element
                // of the very next page, with no extra query per request.
                await AuthEndpoints.ResignAsync(http, WebmailAuthService.WithTheme(http.User, theme.Trim().ToLowerInvariant())).ConfigureAwait(false);
            }
            return Results.Redirect(error is null ? "/settings/appearance?saved=theme" : "/settings/appearance?error=" + Uri.EscapeDataString(error));
        }).RequireAuthorization();

        // rc.11 (UX-07): put back the messages of a delete or move, within ten minutes.
        app.MapPost("/folder/undo", async (HttpContext http, [FromForm] Guid token, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            int restored = await svc.UndoMoveAsync(mailboxId.Value, token, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            string safe = LocalPath.Safe(back);
            return Results.Redirect(safe + (safe.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "undone=" + restored.ToString(CultureInfo.InvariantCulture));
        }).RequireAuthorization();

        // rc.11 (item 40 and 45): Appearance - colour, light or dark, density, layout - applied at once.
        app.MapPost("/settings/appearance", async (HttpContext http, [FromForm] string? colour, [FromForm] string? mode, [FromForm] string? density, [FromForm] string? layout, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string theme = MailboxRow.NormalizeTheme((colour ?? "anjal") + "-" + (mode ?? "light"));
            await svc.SetThemeAsync(mailboxId.Value, theme, ct).ConfigureAwait(false);
            await svc.SetLayoutAsync(mailboxId.Value, layout, density, ct).ConfigureAwait(false);
            await AuthEndpoints.ResignAsync(http, WebmailAuthService.WithTheme(http.User, theme)).ConfigureAwait(false);
            return Results.Redirect("/settings/appearance?saved=appearance");
        }).RequireAuthorization();

        // rc.11 (items 36 and 41): language, time zone, date format, week start.
        app.MapPost("/settings/language", async (HttpContext http, [FromForm] string? language, [FromForm] string? timeZone, [FromForm] string? dateFormat, [FromForm] string? weekStart, [FromForm] string? clock, MailboxService svc, Words words, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            MailboxPreferences? saved = await svc.SetLanguageAndTimeAsync(mailboxId.Value, language, timeZone, dateFormat, weekStart, words, Operators.Is(http.User.Identity?.Name), ct).ConfigureAwait(false);
            if (saved is not null)
            {
                // The cookie carries zone, format and language, so the next page shows them.
                System.Security.Claims.ClaimsPrincipal next = WebmailAuthService.WithPreferences(http.User, saved);
                // Owner, 8 Oct 2026: the person's own clock, or theirs to follow the organisation.
                if (clock is not null)
                {
                    string own = await svc.SetClockAsync(mailboxId.Value, clock, ct).ConfigureAwait(false);
                    next = WebmailAuthService.WithClock(next, own);
                }
                await AuthEndpoints.ResignAsync(http, next).ConfigureAwait(false);
            }
            return Results.Redirect("/settings/language?saved=language");
        }).RequireAuthorization();

        // rc.11 (item 45): three panes, focus, or list only; and density.
        app.MapPost("/settings/layout", async (HttpContext http, [FromForm] string? layout, [FromForm] string? density, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.SetLayoutAsync(mailboxId.Value, layout, density, ct).ConfigureAwait(false);
            return Results.Redirect(LocalPath.Safe(back));
        }).RequireAuthorization();

        // rc.11 (item 5, D-105): the welcome screen answered - never shown again;
        // "Show me around" starts the tour in the Inbox, where its buttons are.
        app.MapPost("/settings/welcome", async (HttpContext http, [FromForm] string? tour, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.SetWelcomeDoneAsync(mailboxId.Value, ct).ConfigureAwait(false);
            return Results.Redirect(string.Equals(tour, "1", StringComparison.Ordinal) ? "/folder/INBOX#tour" : LocalPath.Safe(back));
        }).RequireAuthorization();

        // rc.13 (board SetMail): how long Send waits for Undo, and when Trash is emptied.
        app.MapPost("/settings/mail", async (HttpContext http, [FromForm] int? undo, [FromForm] int? trash, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string? error = await svc.SetMailSettingsAsync(mailboxId.Value, undo, trash, ct).ConfigureAwait(false);
            return Results.Redirect(error is null ? "/settings/mail?saved=mail" : "/settings/mail?error=" + Uri.EscapeDataString(error));
        }).RequireAuthorization();

        // rc.11 (item 14): messages per page.
        app.MapPost("/settings/pagesize", async (HttpContext http, [FromForm] int size, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.SetPageSizeAsync(mailboxId.Value, size, ct).ConfigureAwait(false);
            return Results.Redirect(LocalPath.Safe(back));
        }).RequireAuthorization();

        // rc.11 (item 11): the new-mail sound on or off.
        app.MapPost("/settings/sound", async (HttpContext http, [FromForm] string? on, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.SetNewMailSoundAsync(mailboxId.Value, string.Equals(on, "1", StringComparison.Ordinal), ct).ConfigureAwait(false);
            return Results.Redirect(LocalPath.Safe(back));
        }).RequireAuthorization();

        // Owner, 9 Oct 2026: the order a folder (or search) is sorted in, remembered for it; back to its first page.
        app.MapPost("/settings/sort", async (HttpContext http, [FromForm] string? folder, [FromForm] string? sort, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string name = (folder ?? string.Empty).Trim();
            if (name.Length is > 0 and <= 200)
            {
                await svc.SetSortAsync(mailboxId.Value, name, sort, ct).ConfigureAwait(false);
            }
            return Results.Redirect(LocalPath.Safe(back));
        }).RequireAuthorization();

        // rc.15 (item 58): "Your habits" on the person's dashboard - on unless they turn it off.
        app.MapPost("/settings/habits", async (HttpContext http, [FromForm] string? on, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.SetHabitsAsync(mailboxId.Value, on is "1" or "on", ct).ConfigureAwait(false);
            return Results.Redirect(LocalPath.Safe(back));
        }).RequireAuthorization();

        // rc.11: fold the rail to icons, or open it, and return to the same page.
        app.MapPost("/settings/rail", async (HttpContext http, [FromForm] string? folded, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.SetRailFoldedAsync(mailboxId.Value, string.Equals(folded, "1", StringComparison.Ordinal), ct).ConfigureAwait(false);
            return Results.Redirect(LocalPath.Safe(back));
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
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string? error = await svc.AddCategoryAsync(mailboxId.Value, name ?? string.Empty, ct).ConfigureAwait(false);
            return Results.Redirect(error is null ? "/settings/categories?saved=category" : "/settings/categories?error=" + Uri.EscapeDataString(error));
        }).RequireAuthorization();

        app.MapPost("/settings/categories/delete", async (HttpContext http, [FromForm] Guid categoryId, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string? error = await svc.DeleteCategoryAsync(mailboxId.Value, categoryId, ct).ConfigureAwait(false);
            return Results.Redirect(error is null ? "/settings/categories?saved=categoryremoved" : "/settings/categories?error=" + Uri.EscapeDataString(error));
        }).RequireAuthorization();

        app.MapPost("/settings/categories/rules/delete", async (HttpContext http, [FromForm] Guid ruleId, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.DeleteCategoryRuleAsync(mailboxId.Value, ruleId, ct).ConfigureAwait(false);
            return Results.Redirect("/settings/senders?saved=ruleremoved");
        }).RequireAuthorization();

        app.MapPost("/settings/signature", async (HttpContext http, [FromForm] string? signatureHtml, [FromForm] string? signatureText, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string? error = await svc.SetSignatureAsync(mailboxId.Value, signatureHtml ?? string.Empty, signatureText ?? string.Empty, ct).ConfigureAwait(false);
            return Results.Redirect(error is null ? "/settings/mail?saved=signature" : "/settings/mail?error=" + Uri.EscapeDataString(error));
        }).RequireAuthorization();

        app.MapPost("/settings/senders/add", async (HttpContext http, [FromForm] string? pattern, [FromForm] string? rule, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
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
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
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
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Unauthorized();
            }
            IReadOnlyList<ContactSuggestion> found = await svc.SuggestContactsAsync(mailboxId.Value, q ?? string.Empty, 8, ct).ConfigureAwait(false);
            return Results.Json(found.Select(c => new { name = c.Name, address = c.Address }));
        }).RequireAuthorization();

        // rc.12 (item 64): each person's blanks, for the Send one each preview.
        app.MapGet("/api/merge-preview", async (HttpContext http, string? to, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Unauthorized();
            }
            if (WebmailAuthService.PersonIdOf(http.User) is not Guid person || !await svc.MayMergeAsync(person, ct).ConfigureAwait(false))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            IReadOnlyList<MergePerson> people = await svc.MergePreviewAsync(mailboxId.Value, to ?? string.Empty, ct).ConfigureAwait(false);
            return Results.Json(people.Select(p => new { address = p.Address, values = p.Values }));
        }).RequireAuthorization();

        // rc.12 (items 28, 64): contact groups, offered as one recipient that adds its people.
        app.MapGet("/api/groups", async (HttpContext http, string? q, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Unauthorized();
            }
            string query = (q ?? string.Empty).Trim();
            IReadOnlyList<ContactGroup> groups = await svc.ListGroupsAsync(mailboxId.Value, ct).ConfigureAwait(false);
            return Results.Json(groups.Where(g => query.Length == 0 || g.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .Take(5).Select(g => new { name = g.Name, members = g.Addresses }));
        }).RequireAuthorization();

        app.MapGet("/api/ping", () => Results.NoContent()).RequireAuthorization();

        // rc.11 (item 11): what the page checks every 30 seconds for live update.
        app.MapGet("/api/state", async (HttpContext http, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Unauthorized();
            }
            (long unread, DateTimeOffset? newest) = await svc.InboxStateAsync(mailboxId.Value, ct).ConfigureAwait(false);
            return Results.Json(new { unread, newest = newest?.ToUnixTimeMilliseconds() ?? 0 });
        }).RequireAuthorization();

        // Owner, 9 Oct 2026 (B): the live channel. One tab per browser listens (app.js 5c); it is
        // told within about a second when the Inbox changes, with the same state /api/state
        // gives. It never counts as activity (an /api/ path), checks every 30 seconds that the
        // session still stands, and ends after five minutes so the browser asks again - a
        // session ended meanwhile is not let back in.
        app.MapGet("/api/events", async (HttpContext http, MailboxService svc, SessionRegistry sessions, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            await http.Response.WriteAsync("retry: 5000\n\n", ct).ConfigureAwait(false);
            await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            DateTimeOffset started = DateTimeOffset.UtcNow;
            DateTimeOffset written = started;
            DateTimeOffset checkedAt = started;
            string last = string.Empty;
            try
            {
                while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow - started < LiveStreamLife)
                {
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    if (now - checkedAt > TimeSpan.FromSeconds(30))
                    {
                        checkedAt = now;
                        if (await sessions.CheckAsync(http.User, false, ct).ConfigureAwait(false) != SessionState.Valid)
                        {
                            break;
                        }
                    }
                    (long unread, DateTimeOffset? newest) = await svc.InboxStateAsync(mailboxId.Value, ct).ConfigureAwait(false);
                    string state = System.Text.Json.JsonSerializer.Serialize(new { unread, newest = newest?.ToUnixTimeMilliseconds() ?? 0 });
                    if (state != last)
                    {
                        await http.Response.WriteAsync("event: state\ndata: " + state + "\n\n", ct).ConfigureAwait(false);
                        await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                        last = state;
                        written = now;
                    }
                    else if (now - written > TimeSpan.FromSeconds(25))
                    {
                        // A comment line, so proxies keep the connection open.
                        await http.Response.WriteAsync(": still here\n\n", ct).ConfigureAwait(false);
                        await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                        written = now;
                    }
                    await Task.Delay(LiveStreamStep, ct).ConfigureAwait(false);
                }
                await http.Response.WriteAsync("event: end\ndata: {}\n\n", ct).ConfigureAwait(false);
                await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The browser went away.
            }
        }).RequireAuthorization();
    }

    /// <summary>How long one live channel runs before the browser opens another (owner, 9 Oct 2026, B).</summary>
    public static TimeSpan LiveStreamLife { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often a live channel looks at the Inbox: "within about a second".</summary>
    public static TimeSpan LiveStreamStep { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Serve an embedded asset with its content type and a cache policy:
    /// fonts and logos are immutable for a year, the stylesheets and the
    /// script for an hour with an ETag carrying the build version, so a
    /// redeploy invalidates them.
    /// </summary>
    /// <summary>
    /// Whether HTTP/3 can be offered: not switched off with
    /// <c>ANJAL_WEBMAIL_HTTP3=false</c>, and QUIC available - which on Linux
    /// needs Microsoft's libmsquic and IPv6 enabled in the kernel (.NET's QUIC
    /// uses dual-mode sockets). When it cannot, the webmail serves HTTP/1.1
    /// and HTTP/2 only and says why; it never fails to start over HTTP/3.
    /// </summary>
    /// <param name="note">Why HTTP/3 is off, when it is.</param>
    /// <returns>True to offer HTTP/3.</returns>
    public static bool Http3Available(out string note)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("ANJAL_WEBMAIL_HTTP3"), "false", StringComparison.OrdinalIgnoreCase))
        {
            note = "switched off (ANJAL_WEBMAIL_HTTP3=false)";
            return false;
        }
        if ((OperatingSystem.IsLinux() || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) && System.Net.Quic.QuicListener.IsSupported)
        {
            note = string.Empty;
            return true;
        }
        note = "QUIC is not available on this system (Linux needs libmsquic from packages.microsoft.com and IPv6 enabled - DEPLOY.md section 1d)";
        return false;
    }

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
        return new AssetResult(relativePath, bytes, StaticAssets.ContentType(relativePath), versioned || fixedFile, fingerprint);
    }

    /// <summary>Writes an embedded asset with caching headers and ETag revalidation.</summary>
    private sealed class AssetResult : IResult
    {
        private readonly string relativePath;
        private readonly byte[] bytes;
        private readonly string contentType;
        private readonly bool immutable;
        private readonly string fingerprint;

        public AssetResult(string relativePath, byte[] bytes, string contentType, bool immutable, string fingerprint)
        {
            this.relativePath = relativePath;
            this.bytes = bytes;
            this.contentType = contentType;
            this.immutable = immutable;
            this.fingerprint = fingerprint;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            // Static files only are compressed, Brotli first, then gzip (v1.0.0-rc.7).
            // Each variant has its own ETag, and Vary keeps caches from mixing them.
            byte[] body = this.bytes;
            string? encoding = null;
            if (StaticAssets.IsCompressible(this.relativePath))
            {
                httpContext.Response.Headers.Vary = "Accept-Encoding";
                string accept = httpContext.Request.Headers.AcceptEncoding.ToString();
                foreach (string candidate in new[] { "br", "gzip" })
                {
                    if (Accepts(accept, candidate) && StaticAssets.Compressed(this.relativePath, candidate) is byte[] packed)
                    {
                        body = packed;
                        encoding = candidate;
                        break;
                    }
                }
            }
            string etag = '"' + this.fingerprint + (encoding is null ? string.Empty : "-" + encoding) + '"';
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
            if (encoding is not null)
            {
                httpContext.Response.Headers.ContentEncoding = encoding;
            }
            httpContext.Response.ContentLength = body.Length;
            await httpContext.Response.Body.WriteAsync(body).ConfigureAwait(false);
        }

        /// <summary>Whether an Accept-Encoding value allows <paramref name="coding"/> (present, and not q=0).</summary>
        private static bool Accepts(string acceptEncoding, string coding)
        {
            foreach (string part in acceptEncoding.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] bits = part.Split(';', StringSplitOptions.TrimEntries);
                if (!string.Equals(bits[0], coding, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                return !bits.Skip(1).Any(b => b.Replace(" ", string.Empty, StringComparison.Ordinal) is "q=0" or "q=0.0" or "q=0.00" or "q=0.000");
            }
            return false;
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
            // Only the attachment field: a Send one each list (mergeList) is read on its own.
            if (file.Length <= 0 || string.Equals(file.Name, "mergeList", StringComparison.Ordinal))
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

    private static readonly string[] LocalTimeFormats = { "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss" };

    /// <summary>
    /// The time Send later chose (rc.12, item 8): "tomorrow" or "monday" at
    /// 09:00, or "pick" with a date and time typed in the person's own zone.
    /// Null when nothing usable was given.
    /// </summary>
    internal static DateTimeOffset? SendLaterTime(ZonedClock clock, DateTimeOffset now, string sendAt, string? sendAtLocal)
    {
        (DateTimeOffset tomorrow, DateTimeOffset monday) = MailboxService.SendLaterChoices(clock, now);
        switch (sendAt)
        {
            case "tomorrow":
                return tomorrow;
            case "monday":
                return monday;
            case "pick":
                return DateTime.TryParseExact(sendAtLocal ?? string.Empty, LocalTimeFormats,
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime local)
                    ? MailboxService.AtLocal(clock, local)
                    : null;
            default:
                return null;
        }
    }

    /// <summary>Only allow same-origin relative redirects from form "back" fields.</summary>
    /// <remarks>
    /// Browsers treat a backslash as a slash, so <c>/\evil.example</c> is as
    /// much a protocol-relative URL as <c>//evil.example</c>. Any backslash
    /// or control character in the value refuses it.
    /// </remarks>
    /// <summary>rc.15 (item 37): a "back" that is Anjal's own search page, else null.</summary>
    /// <param name="back">The form's back value.</param>
    internal static string? FromSearch(string? back)
    {
        string safe = SafeBack(back, string.Empty);
        return safe.StartsWith("/search", StringComparison.Ordinal) ? safe : null;
    }

    internal static string SafeBack(string? back, string fallback)
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

    /// <summary>
    /// DEF-050: store the keys that sign the session cookie and the antiforgery
    /// tokens in an explicit folder. Without this, ASP.NET chose one silently
    /// from the service user's home directory, and no document said the keys
    /// existed. The keys stay unencrypted in that folder, which is private to
    /// the service user on an encrypted disk: encrypting them would need a
    /// secret in webmail.env, which deliberately holds none.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="keysDirectory">Folder for the key ring (<c>ANJAL_WEBMAIL_KEYS_DIR</c>), or null or empty to keep ASP.NET's default.</param>
    public static void ConfigureDataProtection(IServiceCollection services, string? keysDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        IDataProtectionBuilder dataProtection = services.AddDataProtection().SetApplicationName("anjal-webmail");
        if (!string.IsNullOrWhiteSpace(keysDirectory))
        {
            dataProtection.PersistKeysToFileSystem(new System.IO.DirectoryInfo(keysDirectory));
        }
    }

    /// <summary>
    /// The antiforgery check for a POST endpoint that binds no form fields
    /// (the framework checks only those that do; see DEF-027).
    /// </summary>
    /// <summary>
    /// A folder's address after an action on it, keeping the page and the All / Unread / Read
    /// choice (DES-11 F7, owner 10 Oct 2026: a person in Unread landed back in All).
    /// </summary>
    internal static string FolderUrl(string name, int? page, string? show) =>
        $"/folder/{Uri.EscapeDataString(name)}?page={System.Math.Max(0, page ?? 0)}" + (show is "unread" or "read" ? "&show=" + show : string.Empty);

    internal static async ValueTask<object?> RequireAntiforgery(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context.HttpContext).ConfigureAwait(false);
        return await next(context).ConfigureAwait(false);
    }

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
