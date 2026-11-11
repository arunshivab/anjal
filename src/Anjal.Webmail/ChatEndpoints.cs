using System.Globalization;
using Anjal.Webmail.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace Anjal.Webmail;

/// <summary>rc.28 (DES-21): chat's forms and the small answers the chat page asks for while it is open.</summary>
internal static class ChatEndpoints
{
    internal static void Map(WebApplication app)
    {
        app.MapPost("/chat/new", async (HttpContext http, [FromForm] string? name, MailboxService svc, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (WebmailAuthService.PersonIdOf(http.User) is not Guid person)
            {
                return Results.Redirect("/sign-in");
            }
            Guid[] people = http.Request.Form["person"].Select(p => Guid.TryParse(p, out Guid g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToArray();
            (Guid? room, string? error) = await svc.StartChatAsync(person, people, name, ct).ConfigureAwait(false);
            return Results.Redirect(room is Guid r ? "/chat/" + r.ToString("N") : "/chat?new=1&err=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error ?? "Not started.")));
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        // Enter sends: from the page's script (an empty answer) or as a plain form (back to the chat).
        app.MapPost("/chat/{id:guid}/send", async (HttpContext http, Guid id, [FromForm] string? text, MailboxService svc, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (WebmailAuthService.PersonIdOf(http.User) is not Guid person)
            {
                return Results.Redirect("/sign-in");
            }
            string? error = await svc.SendChatAsync(person, id, text, ct).ConfigureAwait(false);
            if (http.Request.Headers["X-Anjal-Chat"] == "1")
            {
                return error is null ? Results.NoContent() : Results.Text(error, statusCode: 400);
            }
            return Results.Redirect("/chat/" + id.ToString("N") + (error is null ? "#end" : "?err=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error))));
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        // New messages since the page last looked, and who has seen the reader's last one.
        app.MapGet("/chat/{id:guid}/since", async (HttpContext http, Guid id, long? after, MailboxService svc, CancellationToken ct) =>
        {
            if (WebmailAuthService.PersonIdOf(http.User) is not Guid person)
            {
                return Results.Unauthorized();
            }
            await svc.TouchPresenceAsync(person, ct).ConfigureAwait(false);
            DateTimeOffset from = after is long ticks && ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : DateTimeOffset.MinValue;
            if (await svc.ChatAsync(person, id, from, ct).ConfigureAwait(false) is not ChatView view)
            {
                return Results.NotFound();
            }
            ZonedClock clock = WebmailAuthService.ClockOf(http.User);
            return Results.Json(new
            {
                lines = view.Lines.Select(l => new
                {
                    id = l.Id.ToString("N"),
                    mine = l.Mine,
                    name = l.SenderName,
                    text = l.Text,
                    removed = l.Removed,
                    at = l.At.UtcTicks,
                    time = clock.Time(l.At),
                }),
                seen = view.SeenBy,
                unread = await svc.UnreadChatsAsync(person, ct).ConfigureAwait(false),
            });
        }).RequireAuthorization();

        app.MapPost("/chat/message/{id:guid}/remove", async (HttpContext http, Guid id, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            if (WebmailAuthService.PersonIdOf(http.User) is not Guid person)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.RemoveChatAsync(person, id, http.User.Identity?.Name ?? string.Empty, ct).ConfigureAwait(false);
            return Results.Redirect(Program.SafeBack(back, "/chat"));
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        app.MapPost("/chat/{id:guid}/add", async (HttpContext http, Guid id, MailboxService svc, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (WebmailAuthService.PersonIdOf(http.User) is not Guid person)
            {
                return Results.Redirect("/sign-in");
            }
            Guid[] people = http.Request.Form["person"].Select(p => Guid.TryParse(p, out Guid g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToArray();
            string? error = await svc.AddToChatAsync(person, id, people, ct).ConfigureAwait(false);
            return Results.Redirect("/chat/" + id.ToString("N") + (error is null ? string.Empty : "?err=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error))));
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        app.MapPost("/chat/{id:guid}/leave", async (HttpContext http, Guid id, MailboxService svc, CancellationToken ct) =>
        {
            if (WebmailAuthService.PersonIdOf(http.User) is not Guid person)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.LeaveChatAsync(person, id, ct).ConfigureAwait(false);
            return Results.Redirect("/chat");
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        // The rail's number, every 30 seconds on every page; it also keeps the person online.
        app.MapGet("/chat/unread", async (HttpContext http, MailboxService svc, CancellationToken ct) =>
        {
            if (WebmailAuthService.PersonIdOf(http.User) is not Guid person)
            {
                return Results.Unauthorized();
            }
            await svc.TouchPresenceAsync(person, ct).ConfigureAwait(false);
            return Results.Text((await svc.UnreadChatsAsync(person, ct).ConfigureAwait(false)).ToString(CultureInfo.InvariantCulture));
        }).RequireAuthorization();

        app.MapPost("/presence", async (HttpContext http, [FromForm] string? status, [FromForm] string? note, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            if (WebmailAuthService.PersonIdOf(http.User) is not Guid person)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.SetPresenceAsync(person, status, note, ct).ConfigureAwait(false);
            return Results.Redirect(Program.SafeBack(back, "/chat"));
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);
    }
}
