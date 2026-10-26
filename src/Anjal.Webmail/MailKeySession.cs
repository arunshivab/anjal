using Anjal.Mailbox;
using Anjal.Store;
using Anjal.Webmail.Services;

namespace Anjal.Webmail;

/// <summary>
/// rc.25 (DES-15 §4): the keys of a signed-in person, per request. At sign-in the keys opened with
/// the password are kept for the session, wrapped with a random value that only the browser's
/// cookie holds; each request opens them with the cookie and puts them in the request's keyring.
/// A copy of the database alone opens nothing; signing out removes the session's copy.
/// </summary>
internal static class MailKeySession
{
    /// <summary>The cookie holding the browser's key value (32 random bytes, base64url).</summary>
    internal const string CookieName = "anjal-keys";

    /// <summary>Where the sign-in leaves the opened keys for the session it starts.</summary>
    internal const string UnlockItem = "anjal.unlock";

    /// <summary>The request's keyring, after <see cref="UseMailKeys"/>.</summary>
    internal const string RingItem = "anjal.keyring";

    /// <summary>Open the signed-in person's keys for each request.</summary>
    /// <param name="app">The app.</param>
    internal static void UseMailKeys(this WebApplication app)
    {
        app.Use(async (http, next) =>
        {
            MailKeyring? ring = await OpenAsync(http).ConfigureAwait(false);
            if (ring is null)
            {
                await next().ConfigureAwait(false);
                return;
            }
            http.Items[RingItem] = ring;
            using (MailKeyring.Use(ring))
            {
                try
                {
                    await next().ConfigureAwait(false);
                }
                finally
                {
                    // A response still writing (streamed pages) finishes inside the scope; then the keys go.
                    ring.Dispose();
                }
            }
        });
    }

    /// <summary>
    /// Send a page of mail that cannot be opened here to the unlock page. Placed after what the
    /// organisation asks first (a new password, two-step), which comes before the mail.
    /// </summary>
    /// <param name="app">The app.</param>
    internal static void UseLockedMailPages(this WebApplication app) =>
        app.Use(async (http, next) =>
        {
            if (await LockedPageAsync(http).ConfigureAwait(false))
            {
                return;
            }
            await next().ConfigureAwait(false);
        });

    // DES-15 §4: a page of mail asked for while this request cannot open the mailbox (a passkey
    // sign-in, a restore waiting, a shared mailbox whose key has not reached the person) goes to
    // the unlock page instead of showing empty subjects and unreadable mail.
    private static async Task<bool> LockedPageAsync(HttpContext http)
    {
        if (!HttpMethods.IsGet(http.Request.Method) || !IsMailPage(http.Request.Path) ||
            http.User.Identity?.IsAuthenticated != true || WebmailAuthService.MailboxIdOf(http.User) is not Guid box)
        {
            return false;
        }
        MailboxService svc = http.RequestServices.GetRequiredService<MailboxService>();
        if (!await svc.MailLockedHereAsync(box, http.RequestAborted).ConfigureAwait(false))
        {
            return false;
        }
        http.Response.Redirect("/unlock?back=" + Uri.EscapeDataString(http.Request.Path + http.Request.QueryString));
        return true;
    }

    private static readonly string[] MailPages = { "/folder", "/message", "/compose", "/draft", "/search", "/contacts", "/lajna", "/today", "/dashboard", "/outbox" };

    /// <summary>Whether a path is a page that shows mail.</summary>
    /// <param name="path">The path.</param>
    /// <returns>Whether it is.</returns>
    internal static bool IsMailPage(PathString path) =>
        path == "/" || Array.Exists(MailPages, p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// After a session is started (CompleteAsync): keep the keys the sign-in opened for that session,
    /// and start the session's housekeeping (sealing mail stored before encryption, re-keying what arrived).
    /// </summary>
    /// <param name="http">The request.</param>
    /// <param name="sid">The new session.</param>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="shared">A shared computer.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    internal static async Task AfterSignInAsync(HttpContext http, string sid, Guid mailboxId, bool shared, CancellationToken ct)
    {
        if (http.Items[UnlockItem] is not UnlockResult unlock || unlock.Keys is null)
        {
            return;
        }
        await KeepAsync(http, sid, mailboxId, shared, unlock.Keys, ct).ConfigureAwait(false);
    }

    /// <summary>Keep opened keys for a session and start its housekeeping.</summary>
    /// <param name="http">The request.</param>
    /// <param name="sid">The session.</param>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="shared">A shared computer.</param>
    /// <param name="keys">The keys.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    internal static async Task KeepAsync(HttpContext http, string sid, Guid mailboxId, bool shared, UnlockedKeys keys, CancellationToken ct)
    {
        MailboxService svc = http.RequestServices.GetRequiredService<MailboxService>();
        if (svc.KeyManager is not MailKeyManager km)
        {
            return;
        }
        byte[] token = TokenOf(http, create: true, shared);
        DateTimeOffset expires = DateTimeOffset.UtcNow + (shared ? SessionRegistry.SharedLife : SessionRegistry.OwnLife);
        await km.SaveSessionAsync(sid, mailboxId, token, keys, expires, ct).ConfigureAwait(false);
        svc.HousekeepInBackground(mailboxId, keys);
    }

    /// <summary>The browser's key value, made now when asked to and there is none.</summary>
    /// <param name="http">The request.</param>
    /// <param name="create">Make one when missing.</param>
    /// <param name="shared">A shared computer (the cookie then ends with the browser).</param>
    /// <returns>The value, or empty.</returns>
    internal static byte[] TokenOf(HttpContext http, bool create, bool shared = false)
    {
        if (http.Request.Cookies[CookieName] is string text && text.Length > 0)
        {
            try
            {
                byte[] value = MailEnvelope.FromBase64Url(text);
                if (value.Length == 32)
                {
                    return value;
                }
            }
            catch (FormatException)
            {
                // A damaged cookie: made again below.
            }
        }
        if (http.Items["anjal.keys.new"] is byte[] made)
        {
            return made;
        }
        if (!create)
        {
            return Array.Empty<byte>();
        }
        byte[] token = MailKeyManager.NewSessionToken();
        http.Items["anjal.keys.new"] = token;
        http.Response.Cookies.Append(CookieName, MailEnvelope.Base64Url(token), new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = shared ? null : SessionRegistry.OwnLife,
            IsEssential = true,
        });
        return token;
    }

    private static async Task<MailKeyring?> OpenAsync(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated != true ||
            SessionRegistry.SessionOf(http.User) is not string sid ||
            WebmailAuthService.PersonIdOf(http.User) is not Guid mailboxId)
        {
            return null;
        }
        byte[] token = TokenOf(http, create: false);
        if (token.Length == 0)
        {
            return null;
        }
        MailboxService svc = http.RequestServices.GetRequiredService<MailboxService>();
        if (svc.KeyManager is not MailKeyManager km)
        {
            return null;
        }
        UnlockedKeys? keys = await km.LoadSessionAsync(sid, mailboxId, token, http.RequestAborted).ConfigureAwait(false);
        if (keys is null)
        {
            return null;
        }
        var ring = new MailKeyring();
        try
        {
            await km.FillAsync(ring, mailboxId, keys, http.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            // The keyring holds its own copies; these go now.
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(keys.PrivateKey);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(keys.DataKey);
        }
        return ring;
    }
}
