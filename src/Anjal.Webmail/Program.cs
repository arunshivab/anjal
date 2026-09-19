using System.Globalization;
using System.Security.Claims;
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
///   ANJAL_WEBMAIL_SECURE    - "true" to mark the session cookie Secure (set when behind HTTPS).
/// </summary>
public static class Program
{
    private const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;

    /// <summary>Entry point.</summary>
    /// <param name="args">Command-line arguments (passed through to the host builder).</param>
    public static async Task<int> Main(string[] args)
    {
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

        WebApplication app = CreateApp(args, store, new MaildirStore(maildirRoot, hostname), hostname, $"http://{bind}:{port}");
        Console.WriteLine($"Anjal webmail listening on http://{bind}:{port}/");
        Console.WriteLine(pg is null ? "Using in-memory store (demo only)." : "Using PostgreSQL store.");
        Console.WriteLine($"Maildir root: {maildirRoot}");
        await app.RunAsync().ConfigureAwait(false);
        return 0;
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
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

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
            options.Cookie.SecurePolicy = secure ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
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
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        MapEndpoints(app);
        app.MapRazorComponents<App>();
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
