using Anjal.Store;
using Anjal.Webmail.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace Anjal.Webmail;

/// <summary>
/// rc.27 (DES-17 §2): the forms of shared desks - assign, take, mark done, notes, "is replying",
/// opening a desk at a mail, the desk's settings and the organisation's working week.
/// </summary>
internal static class DeskEndpoints
{
    internal static void Map(WebApplication app)
    {
        // The Desk block's buttons (DES-17 §2.4, §2.5, §2.8): the person acts inside the desk.
        app.MapPost("/desk/act", async (HttpContext http, [FromForm] Guid message, [FromForm] string? action, [FromForm] string? to, [FromForm] string? text, [FromForm] string? back,
            MailboxService svc, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (WebmailAuthService.PersonIdOf(http.User) is not Guid person || WebmailAuthService.ActingOf(http.User) is not Guid desk)
            {
                return Results.Redirect("/folder/INBOX");
            }
            Guid? target = Guid.TryParse(to, out Guid t) ? t : null;
            string? error = await svc.DeskActAsync(desk, person, message, action ?? string.Empty, target, text, ct).ConfigureAwait(false);
            string after = Program.SafeBack(back, "/folder/INBOX?open=" + message);
            return Results.Redirect(error is null ? after : after + (after.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "deskerr=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error)));
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        // "Kavya is replying" (DES-17 §2.7): the reply form says so every 30 seconds, and when it closes.
        app.MapPost("/desk/replying", async (HttpContext http, [FromForm] Guid message, [FromForm] string? on, MailboxService svc, CancellationToken ct) =>
        {
            if (WebmailAuthService.PersonIdOf(http.User) is Guid person && WebmailAuthService.ActingOf(http.User) is Guid desk
                && await svc.SharedRightCachedAsync(person, desk, ct).ConfigureAwait(false) is "send" or "manage")
            {
                await svc.SetReplyingAsync(desk, person, message, on == "1", ct).ConfigureAwait(false);
            }
            return Results.NoContent();
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        // Open a desk at one of its mails: from Today's "Assigned to you" and from a desk pop-up, both
        // forms. It changes which mailbox the person is looking at, as the account menu does, so it is
        // never a GET (review, 28 Oct: a link from another site would switch the session).
        app.MapPost("/desk/open", (HttpContext http, [FromForm] Guid desk, [FromForm] Guid message, MailboxService svc, AuditTrail audit, CancellationToken ct) =>
            OpenAsync(http, desk, message, svc, audit, ct)).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        // The desk's own settings (D-304, D-315): the organisation's administrators, and the desk's managers.
        app.MapPost("/desk/settings/{id:guid}", async (HttpContext http, Guid id, [FromForm] string? assignBy, [FromForm] string? target, [FromForm] string? week,
            [FromForm] string? from, [FromForm] string? until, [FromForm] string? zone, [FromForm] string? back,
            MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (WebmailAuthService.PersonIdOf(http.User) is not Guid person || await svc.GetMailboxAnyAsync(id, ct).ConfigureAwait(false) is not MailboxRow desk
                || await svc.GetContextAsync(person, ct).ConfigureAwait(false) is not { } me || me.Tenant.Id != desk.TenantId)
            {
                return Results.NotFound();
            }
            bool admin = await svc.IsOrgAdminAsync(person, ct).ConfigureAwait(false);
            if (!admin && await svc.SharedRightAsync(person, id, ct).ConfigureAwait(false) != "manage")
            {
                return Results.NotFound();
            }
            WorkWeek? own = week == "own" ? WeekFrom(http.Request.Form["day"], from, until, zone) : null;
            string? error = await svc.SetDeskSettingsAsync(me.Tenant, id, assignBy ?? "any", target ?? "1d", own, ct).ConfigureAwait(false);
            string after = Program.SafeBack(back, admin ? "/org/shared?sel=" + id : "/folder/INBOX");
            if (error is not null)
            {
                return Results.Redirect(after + (after.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error)));
            }
            await audit.RecordAsync(me.Mailbox.Address, "desk.settings.changed", desk.Address, ClientOf(http),
                $"assign {assignBy ?? "any"}, reply within {target ?? "1d"}, {(own is null ? "organisation's week" : "own week")}").ConfigureAwait(false);
            return Results.Redirect(after + (after.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "saved=desk");
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        // The organisation's working week (D-315): administrators only.
        app.MapPost("/org/work-week", async (HttpContext http, [FromForm] string? from, [FromForm] string? until, [FromForm] string? zone,
            MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OrgEndpoints.AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            WorkWeek week = WeekFrom(http.Request.Form["day"], from, until, zone);
            string? error = await svc.SetWorkWeekAsync(a.Tenant, week, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Results.Redirect("/org/shared?e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error)));
            }
            await audit.RecordAsync(a.Me.Address, "org.workweek.changed", a.Tenant.Slug, ClientOf(http),
                string.Join(",", week.Days) + " " + week.From + "-" + week.To + " " + week.TimeZone).ConfigureAwait(false);
            return Results.Redirect("/org/shared?saved=week");
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);
    }

    private static WorkWeek WeekFrom(Microsoft.Extensions.Primitives.StringValues day, string? from, string? until, string? zone) => new()
    {
        Days = day.Select(d => int.TryParse(d, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : -1).Where(n => n is >= 0 and <= 6).Distinct().ToList(),
        From = (from ?? string.Empty).Trim(),
        To = (until ?? string.Empty).Trim(),
        TimeZone = string.IsNullOrWhiteSpace(zone) ? MailboxPreferences.DefaultTimeZone : zone.Trim(),
    };

    private static async Task<IResult> OpenAsync(HttpContext http, Guid desk, Guid message, MailboxService svc, AuditTrail audit, CancellationToken ct)
    {
        if (WebmailAuthService.PersonIdOf(http.User) is not Guid person)
        {
            return Results.Redirect("/sign-in");
        }
        if (await svc.SharedRightAsync(person, desk, ct).ConfigureAwait(false) is null)
        {
            return Results.Redirect("/folder/INBOX?sharedclosed=1");
        }
        if (WebmailAuthService.ActingOf(http.User) != desk)
        {
            await AuthEndpoints.ResignAsync(http, WebmailAuthService.WithActing(http.User, desk)).ConfigureAwait(false);
            string box = (await svc.GetContextAsync(desk, ct).ConfigureAwait(false))?.Mailbox.Address ?? desk.ToString();
            await audit.RecordAsync(http.User.Identity?.Name ?? string.Empty, "webmail.shared.opened", box, ClientOf(http)).ConfigureAwait(false);
        }
        return Results.Redirect("/folder/INBOX?open=" + message);
    }

    private static string ClientOf(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
