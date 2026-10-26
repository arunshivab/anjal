using Anjal.Webmail.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace Anjal.Webmail;

/// <summary>
/// rc.25 (DES-15 §6): the organisation console's Encryption forms - restoring a person's mail, the
/// recovery sheet (print, type back, use), and the break-glass switch. Only an administrator of the
/// organisation reaches them, each is written to the activity log, and none reads anyone's mail.
/// </summary>
internal static class OrgEncryptionEndpoints
{
    internal static void Map(WebApplication app)
    {
        app.MapPost("/org/encryption/restore", async (HttpContext http, [FromForm] Guid person, MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OrgEndpoints.AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            string? error = await svc.RestoreMailAsync(a.Tenant, a.Me.Id, person, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Results.Redirect("/org/encryption?e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error switch
                {
                    "no-key" => "Your session does not hold the organisation's recovery key. Use the recovery sheet first.",
                    "wrong-key" => "Their mail key did not open with the organisation's recovery key. Tell the Anjal operator.",
                    _ => "Nothing is waiting for that person.",
                })));
            }
            string address = (await svc.GetContextAsync(person, ct).ConfigureAwait(false))?.Mailbox.Address ?? person.ToString();
            await audit.RecordAsync(a.Me.Address, "org.encryption.restored", address, Client(http)).ConfigureAwait(false);
            return Results.Redirect("/org/encryption?saved=restored");
        }).RequireAuthorization();

        app.MapPost("/org/encryption/sheet", async (HttpContext http, MailboxService svc, SessionRegistry sessions, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OrgEndpoints.AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            string? sheet = await svc.NewSheetAsync(a.Tenant, a.Me.Id, ct).ConfigureAwait(false);
            if (sheet is null)
            {
                return Results.Redirect("/org/encryption?e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, "Your session does not hold the organisation's recovery key. Use the recovery sheet first.")));
            }
            await audit.RecordAsync(a.Me.Address, "org.encryption.sheet-issued", a.Tenant.Slug, Client(http)).ConfigureAwait(false);
            sessions.Reveal(SessionRegistry.SessionOf(http.User) ?? string.Empty, new[] { sheet });
            return Results.Redirect("/org/encryption?sheet=1");
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        app.MapPost("/org/encryption/confirm", async (HttpContext http, [FromForm] string? typed, MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OrgEndpoints.AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            if (!await svc.ConfirmSheetAsync(a.Tenant, typed ?? string.Empty, ct).ConfigureAwait(false))
            {
                return Results.Redirect("/org/encryption?e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, "That is not the sheet just printed. Check each group of four; the last group finds a mistyped character.")));
            }
            await audit.RecordAsync(a.Me.Address, "org.encryption.sheet-confirmed", a.Tenant.Slug, Client(http)).ConfigureAwait(false);
            return Results.Redirect("/org/encryption?saved=sheet");
        }).RequireAuthorization();

        app.MapPost("/org/encryption/use-sheet", async (HttpContext http, [FromForm] string? typed, MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OrgEndpoints.AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            string? error = await svc.UseSheetAsync(a.Tenant, a.Me.Id, typed ?? string.Empty, ct).ConfigureAwait(false);
            if (error is not null)
            {
                await audit.RecordAsync(a.Me.Address, "org.encryption.sheet-refused", a.Tenant.Slug, Client(http)).ConfigureAwait(false);
                return Results.Redirect("/org/encryption?e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error == "locked"
                    ? "Your own mail must be open first: sign out and sign in with your password, then use the sheet."
                    : "That sheet does not open the recovery key. Check each group of four; the last group finds a mistyped character.")));
            }
            await audit.RecordAsync(a.Me.Address, "org.encryption.sheet-used", a.Tenant.Slug, Client(http)).ConfigureAwait(false);
            return Results.Redirect("/org/encryption?saved=used");
        }).RequireAuthorization();

        app.MapPost("/org/encryption/breakglass", async (HttpContext http, [FromForm] string? on, [FromForm] string? understood, MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OrgEndpoints.AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            bool turnOn = on == "1";
            if (!turnOn && understood != "1")
            {
                return Results.Redirect("/org/encryption?e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, "Tick the box to say you understand before switching it off.")));
            }
            await svc.SetBreakGlassAsync(a.Tenant, turnOn, ct).ConfigureAwait(false);
            await audit.RecordAsync(a.Me.Address, turnOn ? "org.encryption.breakglass-on" : "org.encryption.breakglass-off", a.Tenant.Slug, Client(http)).ConfigureAwait(false);
            return Results.Redirect("/org/encryption?saved=" + (turnOn ? "glass-on" : "glass-off"));
        }).RequireAuthorization();
    }

    private static string Client(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
