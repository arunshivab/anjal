using System.Globalization;
using System.Text;
using Anjal.Store;
using Anjal.Webmail.Services;
using Microsoft.AspNetCore.Mvc;

namespace Anjal.Webmail;

/// <summary>
/// rc.15, item 65 (b), as the owner decided on 10 Oct 2026 (DES-11 D5): offline, the mail LIST
/// only - never a message or an attachment. A person who turns it on (never on a shared computer,
/// and only where their organisation allows it) sets an offline code, or uses a passkey where the
/// device can; the service worker (<c>sw.js</c>) keeps the list locked with it, so it cannot be
/// read without the person, not even on their own unlocked computer. Five wrong tries remove it;
/// so do seven days without reaching Anjal, signing out, and a session ended from elsewhere.
/// </summary>
internal static class OfflineEndpoints
{
    /// <summary>How many of the newest messages of the Inbox are kept.</summary>
    internal const int InboxKept = 200;

    /// <summary>How many of the newest messages of Sent are kept.</summary>
    internal const int SentKept = 50;


    internal static void Map(WebApplication app)
    {
        app.MapPost("/settings/offline", async (HttpContext http, [FromForm] string? on, MailboxService svc, AuditTrail audit, CancellationToken ct) =>
        {
            if (WebmailAuthService.PersonIdOf(http.User) is not Guid person)
            {
                return Results.Redirect("/sign-in");
            }
            (bool allowed, bool _) = await svc.OfflineMailAsync(person, SessionRegistry.IsShared(http.User), ct).ConfigureAwait(false);
            bool turnOn = on == "1" && allowed;
            await svc.SetOfflineMailAsync(person, turnOn, ct).ConfigureAwait(false);
            string who = http.User.Identity?.Name ?? string.Empty;
            await audit.RecordAsync(who, turnOn ? "webmail.offline.on" : "webmail.offline.off", who, http.Connection.RemoteIpAddress?.ToString() ?? "unknown").ConfigureAwait(false);
            return Results.Redirect("/settings/mail?saved=" + (turnOn ? "offline" : "offlineoff"));
        }).RequireAuthorization();

        // DES-11 D5 (owner, 10 Oct 2026): only the list is kept offline - who, subject (unless the
        // organisation keeps subjects off devices), when, and the marks - never a message or an
        // attachment. The worker locks it with the person's offline code or passkey.
        app.MapGet("/offline/list", async (HttpContext http, MailboxService svc, CancellationToken ct) =>
        {
            if (WebmailAuthService.PersonIdOf(http.User) is not Guid person)
            {
                return Results.Unauthorized();
            }
            (bool _, bool on) = await svc.OfflineMailAsync(person, SessionRegistry.IsShared(http.User), ct).ConfigureAwait(false);
            if (!on)
            {
                // Off (or a shared computer): the worker removes every copy.
                return Results.NotFound();
            }
            http.Response.Headers.CacheControl = "no-store";
            if (WebmailAuthService.ActingOf(http.User) is not null)
            {
                // DES-11 F11: in a shared mailbox the person's own copy is left as it is, never wiped.
                return Results.NoContent();
            }
            bool subjects = (await svc.SignInPolicyForAsync(person, ct).ConfigureAwait(false)).OfflineSubjects;
            var messages = new List<object>();
            foreach ((string folder, int keep) in new[] { (FolderRow.Inbox, InboxKept), ("Sent", SentKept) })
            {
                FolderRow? f = await svc.GetFolderAsync(person, folder, ct).ConfigureAwait(false);
                if (f is null)
                {
                    continue;
                }
                (IReadOnlyList<MessageRow> items, long _) = await svc.ListMessagesAsync(person, f.Id, 0, keep, ct).ConfigureAwait(false);
                messages.AddRange(items.Select(m => new
                {
                    id = m.Id,
                    f = folder,
                    s = subjects ? Anjal.Mime.EncodedWordDecoder.Decode(m.Subject) : string.Empty,
                    w = folder == "Sent" ? Recipient(m) : Sender(m),
                    t = m.ReceivedAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                    u = !m.Seen,
                    a = m.HasAttachments,
                }));
            }
            return Results.Json(new
            {
                at = DateTimeOffset.UtcNow.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                session = SessionMark(SessionRegistry.SessionOf(http.User)),
                subjects,
                expireDays = ExpireDays,
                messages,
            });
        }).RequireAuthorization();
    }

    /// <summary>Days a device keeps its locked list without reaching Anjal; then it removes it (DES-11 D5).</summary>
    internal const int ExpireDays = 7;

    // A mark of the session the list was kept for, so a copy never outlives it (DES-11 F11).
    private static string SessionMark(string? sid) =>
        sid is null ? string.Empty : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("offline:" + sid)))[..16];

    private static string Recipient(MessageRow m)
    {
        IReadOnlyList<Anjal.Mime.MailAddress> parsed = Anjal.Mime.AddressParser.Parse(Anjal.Mime.EncodedWordDecoder.Decode(m.ToHeader));
        return parsed.Count > 0 ? (parsed[0].DisplayName.Length > 0 ? parsed[0].DisplayName : parsed[0].Address) : string.Empty;
    }

    private static string Sender(MessageRow m)
    {
        IReadOnlyList<Anjal.Mime.MailAddress> parsed = Anjal.Mime.AddressParser.Parse(Anjal.Mime.EncodedWordDecoder.Decode(m.FromHeader));
        return parsed.Count > 0 ? (parsed[0].DisplayName.Length > 0 ? parsed[0].DisplayName : parsed[0].Address) : m.EnvelopeFrom;
    }
}
