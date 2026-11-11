using System.Globalization;
using Anjal.Webmail.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace Anjal.Webmail;

/// <summary>
/// rc.28 (DES-19): the secure link's forms - for the outside person (ask for a code, type it, download a
/// file, reply), signed out; and for the sender (stop a link, send a new one).
/// </summary>
internal static class SecureLinkEndpoints
{
    /// <summary>The visit's cookie: which link, until when, sealed by the webmail.</summary>
    internal const string VisitCookie = "anjal-sl";

    private const string VisitPurpose = "Anjal.SecureLink.Visit.v1";

    internal static void Map(WebApplication app)
    {
        // Step 2: mail me the code.
        app.MapPost("/s/{token}/code", async (HttpContext http, string token, MailboxService svc, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            string? error = await svc.MailSecureCodeAsync(token, ClientOf(http), ct).ConfigureAwait(false);
            return Results.Redirect($"/s/{Uri.EscapeDataString(token)}" + (error is null ? "?sent=1" : "?err=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error))));
        }).AllowAnonymous().AddEndpointFilter(Program.RequireAntiforgery);

        // Step 3: the code.
        app.MapPost("/s/{token}/open", async (HttpContext http, string token, [FromForm] string? code, MailboxService svc, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            (bool right, int left) = await svc.CheckSecureCodeAsync(token, code, ClientOf(http), ct).ConfigureAwait(false);
            if (!right)
            {
                return Results.Redirect($"/s/{Uri.EscapeDataString(token)}?sent=1&left={left.ToString(CultureInfo.InvariantCulture)}");
            }
            if (await svc.SecureLinkPageAsync(token, ct).ConfigureAwait(false) is SecureLinkPage page)
            {
                StartVisit(http, dp, page.Link.Id);
            }
            return Results.Redirect($"/s/{Uri.EscapeDataString(token)}");
        }).AllowAnonymous().AddEndpointFilter(Program.RequireAntiforgery);

        // A file of the mail, during a visit; never a program.
        app.MapGet("/s/{token}/file/{index:int}", async (HttpContext http, string token, int index, MailboxService svc, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await svc.SecureLinkPageAsync(token, ct).ConfigureAwait(false) is not SecureLinkPage page || !InVisit(http, dp, page.Link.Id)
                || await svc.SecureFileAsync(token, index, ct).ConfigureAwait(false) is not { } file)
            {
                return Results.NotFound();
            }
            return Results.File(file.Bytes, "application/octet-stream", file.Name);
        }).AllowAnonymous();

        // The reply (DES-19 §5): to the sender's Inbox; it ends the visit.
        app.MapPost("/s/{token}/reply", async (HttpContext http, string token, [FromForm] string? text, IFormFileCollection files, MailboxService svc, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await svc.SecureLinkPageAsync(token, ct).ConfigureAwait(false) is not SecureLinkPage page || VisitStart(http, dp, page.Link.Id) is not DateTimeOffset visitStart)
            {
                return Results.Redirect($"/s/{Uri.EscapeDataString(token)}");
            }
            long declared = files.Sum(f => Math.Max(0, f.Length));
            if (declared > Program.MaxAttachmentBytes)
            {
                return Results.Redirect($"/s/{Uri.EscapeDataString(token)}?err=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, Program.AttachmentsTooLarge)));
            }
            var attached = new List<(string FileName, string ContentType, byte[] Bytes)>();
            foreach (IFormFile f in files)
            {
                if (f.Length <= 0)
                {
                    continue;
                }
                using var ms = new MemoryStream();
                await f.CopyToAsync(ms, ct).ConfigureAwait(false);
                attached.Add((Path.GetFileName(f.FileName), f.ContentType, ms.ToArray()));
            }
            string? error = await svc.SecureReplyAsync(token, text, attached, ClientOf(http), visitStart, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Results.Redirect($"/s/{Uri.EscapeDataString(token)}?err=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error)));
            }
            http.Response.Cookies.Delete(VisitCookie, new CookieOptions { Path = "/s/" });
            return Results.Redirect($"/s/{Uri.EscapeDataString(token)}?replied=1");
        }).AllowAnonymous().AddEndpointFilter(Program.RequireAntiforgery);

        // The sender: stop a link, or send a new one (DES-19 §6).
        app.MapPost("/securelink/{id:guid}/stop", async (HttpContext http, Guid id, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            if (WebmailAuthService.MailboxIdOf(http.User) is not Guid mailboxId)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.StopSecureLinkAsync(mailboxId, id, http.User.Identity?.Name ?? string.Empty, ct).ConfigureAwait(false);
            return Results.Redirect(Program.SafeBack(back, "/folder/Sent"));
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        app.MapPost("/securelink/{id:guid}/again", async (HttpContext http, Guid id, [FromForm] string? back, MailboxService svc, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (WebmailAuthService.MailboxIdOf(http.User) is not Guid mailboxId)
            {
                return Results.Redirect("/sign-in");
            }
            string? error = await svc.RenewSecureLinkAsync(mailboxId, id, http.User.Identity?.Name ?? string.Empty, OrgEndpoints.BaseUrl(http), ct).ConfigureAwait(false);
            string after = Program.SafeBack(back, "/folder/Sent");
            return Results.Redirect(error is null ? after : after + (after.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "linkerr=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error)));
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);
    }

    /// <summary>Whether this browser typed the right code for this link within the last 30 minutes.</summary>
    /// <param name="http">The request.</param>
    /// <param name="dp">Data protection.</param>
    /// <param name="linkId">The link.</param>
    /// <returns>True during a visit.</returns>
    internal static bool InVisit(HttpContext http, IDataProtectionProvider dp, Guid linkId) => VisitStart(http, dp, linkId) is not null;

    /// <summary>When this browser's visit to the link began (the right code typed), or null outside a visit.</summary>
    /// <param name="http">The request.</param>
    /// <param name="dp">Data protection.</param>
    /// <param name="linkId">The link.</param>
    /// <returns>The start, or null.</returns>
    internal static DateTimeOffset? VisitStart(HttpContext http, IDataProtectionProvider dp, Guid linkId)
    {
        string? cookie = http.Request.Cookies[VisitCookie];
        if (string.IsNullOrEmpty(cookie))
        {
            return null;
        }
        try
        {
            string value = dp.CreateProtector(VisitPurpose).ToTimeLimitedDataProtector().Unprotect(cookie);
            int bar = value.IndexOf('|', StringComparison.Ordinal);
            return bar > 0 && string.Equals(value[..bar], linkId.ToString("N"), StringComparison.Ordinal)
                && long.TryParse(value[(bar + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
                ? new DateTimeOffset(ticks, TimeSpan.Zero)
                : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static void StartVisit(HttpContext http, IDataProtectionProvider dp, Guid linkId)
    {
        string value = dp.CreateProtector(VisitPurpose).ToTimeLimitedDataProtector().Protect(linkId.ToString("N") + "|" + DateTimeOffset.UtcNow.UtcTicks.ToString(CultureInfo.InvariantCulture), MailboxService.SecureVisitLife);
        http.Response.Cookies.Append(VisitCookie, value, new CookieOptions
        {
            Path = "/s/",
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            MaxAge = MailboxService.SecureVisitLife,
            IsEssential = true,
        });
    }

    private static string ClientOf(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
