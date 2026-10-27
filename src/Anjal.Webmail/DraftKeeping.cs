using Anjal.Webmail.Services;
using Microsoft.AspNetCore.DataProtection;

namespace Anjal.Webmail;

/// <summary>
/// rc.26 (item 80, brief 11a): the session that ends while writing keeps the draft. The compose page
/// carries a keep token (bound to the mailbox, good for a day, renewed with every autosave); when the
/// session has ended, the page sends what is written with that token, and the text is kept as a new
/// draft - sealed to the mailbox's public key like any arriving mail, so no key of the person's is
/// needed. Signing in again opens it, for the same person only. Files added since the last save are
/// not carried; the earlier draft keeps them.
/// </summary>
internal static class DraftKeeping
{
    /// <summary>The cookie naming the kept draft until the next sign-in.</summary>
    internal const string KeptCookie = "anjal-kept";

    private const string Purpose = "anjal.draft-keep.v1";

    /// <summary>A keep token for a mailbox.</summary>
    /// <param name="dp">Data protection.</param>
    /// <param name="mailboxId">The mailbox.</param>
    /// <returns>The token.</returns>
    internal static string Token(IDataProtectionProvider dp, Guid mailboxId) =>
        dp.CreateProtector(Purpose).ToTimeLimitedDataProtector().Protect(mailboxId.ToString("N") + "|" + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), TimeSpan.FromDays(1));

    /// <summary>The mailbox a keep token stands for, or null when it is not one or has expired.</summary>
    /// <param name="dp">Data protection.</param>
    /// <param name="token">The token.</param>
    /// <returns>The mailbox.</returns>
    internal static (Guid Mailbox, DateTimeOffset Issued)? MailboxOf(IDataProtectionProvider dp, string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 2000)
        {
            return null;
        }
        try
        {
            string[] plain = dp.CreateProtector(Purpose).ToTimeLimitedDataProtector().Unprotect(token).Split('|');
            return plain.Length == 2 && Guid.TryParseExact(plain[0], "N", out Guid id)
                && long.TryParse(plain[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long issued)
                ? (id, DateTimeOffset.FromUnixTimeSeconds(issued))
                : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The kept draft for this person, once; the cookie goes either way.</summary>
    /// <param name="http">The request.</param>
    /// <param name="mailboxId">Who signed in.</param>
    /// <returns>The draft, or null.</returns>
    internal static Guid? TakeKept(HttpContext http, Guid mailboxId)
    {
        string? value = http.Request.Cookies[KeptCookie];
        if (value is null)
        {
            return null;
        }
        http.Response.Cookies.Delete(KeptCookie);
        string[] parts = value.Split(':');
        return parts.Length == 2 && Guid.TryParse(parts[0], out Guid box) && box == mailboxId && Guid.TryParse(parts[1], out Guid draft) ? draft : null;
    }

    /// <summary>Map <c>POST /draft/keep</c>.</summary>
    /// <param name="app">The app.</param>
    internal static void MapDraftKeeping(this WebApplication app)
    {
        // No session and no antiforgery token: the keep token, which only the page that was open holds,
        // proves the mailbox; the X-Requested-With header keeps other sites' forms out.
        app.MapPost("/draft/keep", async (HttpContext http, IDataProtectionProvider dp, MailboxService svc, SessionRegistry sessions, CancellationToken ct) =>
        {
            if (http.Request.Headers["X-Requested-With"] != "anjal" || !http.Request.HasFormContentType)
            {
                return Results.BadRequest();
            }
            IFormCollection form = await http.Request.ReadFormAsync(ct).ConfigureAwait(false);
            // rc.26 review: not after the sessions were ended on purpose, and never for a stopped mailbox.
            if (MailboxOf(dp, form["keep"].ToString()) is not (Guid mailboxId, DateTimeOffset issued)
                || sessions.EndedOnPurpose(mailboxId) is DateTimeOffset ended && ended >= issued
                || await svc.GetMailboxAnyAsync(mailboxId, ct).ConfigureAwait(false) is not { Enabled: true })
            {
                return Results.Json(new { }, statusCode: 403);
            }
            string Clip(string name, int max) { string v = form[name].ToString(); return v.Length > max ? v[..max] : v; }
            var request = new ComposeRequest
            {
                To = Clip("to", 4000),
                Cc = Clip("cc", 4000),
                Bcc = Clip("bcc", 4000),
                Subject = Clip("subject", 998),
                Body = Clip("body", 2_000_000),
                BodyHtml = Clip("bodyHtml", 4_000_000),
                InReplyTo = Clip("inReplyTo", 998),
                References = Clip("references", 8000),
            };
            if ((request.To + request.Subject + request.Body).Trim().Length == 0)
            {
                return Results.Json(new { });
            }
            Guid? saved = await svc.SaveDraftAsync(mailboxId, request, ct).ConfigureAwait(false);
            if (saved is null)
            {
                return Results.Json(new { });
            }
            http.Response.Cookies.Append(KeptCookie, mailboxId.ToString("N") + ":" + saved.Value.ToString("N"), new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromDays(1),
                IsEssential = true,
            });
            return Results.Json(new { id = saved.Value.ToString() });
        }).AllowAnonymous().DisableAntiforgery();
    }
}
