using Anjal.Webmail.Services;
using Microsoft.AspNetCore.Mvc;

namespace Anjal.Webmail;

/// <summary>
/// rc.16 (design review 8, [5b]): Today's quick reply. The reply goes to the sender only, with
/// "Re:" on the subject, the person's signature and the original quoted under it - as a Reply
/// started in Compose would - and waits for Undo as long as the person chose. Then the next
/// unread opens.
/// </summary>
internal static class TodayEndpoints
{
    internal static void Map(WebApplication app)
    {
        app.MapPost("/today/reply", async (HttpContext http, [FromForm] Guid id, [FromForm] string? body, [FromForm] string? next, [FromForm] string? undo, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.MailboxIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string after = Program.SafeBack(next, "/dashboard/today");
            if (string.IsNullOrWhiteSpace(body))
            {
                return Results.Redirect("/dashboard/today?open=" + id);
            }
            ComposePrefill? prefill = await svc.PrefillAsync(mailboxId.Value, id, MailboxService.PrefillKind.Reply, ct).ConfigureAwait(false);
            if (prefill is null)
            {
                return Results.NotFound();
            }
            (string start, string _) = await svc.StartBodyAsync(mailboxId.Value, prefill.Body, ct).ConfigureAwait(false);
            var request = new ComposeRequest
            {
                To = prefill.To,
                Subject = prefill.Subject,
                Body = body.TrimEnd() + "\n" + start,
                InReplyTo = prefill.InReplyTo,
            };
            request.Sender = await svc.SenderForAsync(WebmailAuthService.PersonIdOf(http.User), mailboxId.Value, ct).ConfigureAwait(false);
            if (WebmailAuthService.PersonIdOf(http.User) is Guid writer)
            {
                await svc.RememberRecipientsAsync(writer, http.User.Identity?.Name ?? string.Empty, request, ct).ConfigureAwait(false);
            }
            int wait = (await svc.GetMailSettingsAsync(mailboxId.Value, ct).ConfigureAwait(false)).UndoSeconds;
            if (string.Equals(undo, "1", StringComparison.Ordinal) && wait > 0)
            {
                (string? holdError, Guid? held) = await svc.HoldAsync(mailboxId.Value, request, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);
                if (holdError is not null || held is null)
                {
                    return Results.Redirect("/compose?reply=" + id);
                }
                return Results.Redirect(after + (after.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "held=" + held.Value + "&wait=" + wait.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            string? error = await svc.SendAsync(mailboxId.Value, request, ct).ConfigureAwait(false);
            return Results.Redirect(error is null ? after : "/compose?reply=" + id);
        }).RequireAuthorization();
    }
}
