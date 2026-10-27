using System.Globalization;
using Anjal.Store;
using Anjal.Webmail.AppApi;
using Anjal.Webmail.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace Anjal.Webmail;

/// <summary>
/// rc.26 (DES-14 §28.6): the console's forms for an application's API page - HTTP keys, signing
/// keys, encryption keys, inbox addresses, webhooks, limits and templates - and the operator's
/// patient-data mark and private-address permission. Every change is in the activity log.
/// </summary>
internal static class OrgAppApiEndpoints
{
    /// <summary>Map the forms.</summary>
    /// <param name="app">The application.</param>
    internal static void Map(WebApplication app)
    {
        app.MapPost("/org/apps/{id}/http-key", async (HttpContext http, string id, [FromForm] string? note, [FromForm] string? expires, [FromForm] string? sandbox,
            MailboxService svc, SessionRegistry sessions, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OrgEndpoints.AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            // rc.26 check (owner, 27 Oct): the key ends when the chosen day ends in the administrator's own clock.
            // (Parsed as a local DateTime and given offset 0, it threw on any computer not set to UTC.)
            DateTimeOffset? ends = DateTime.TryParseExact(expires ?? string.Empty, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d)
                ? MailboxService.AtLocal(WebmailAuthService.ClockOf(http.User), d.Date.AddDays(1))
                : null;
            (string? error, string? key, string? fingerprint) = await svc.NewHttpKeyAsync(a.Tenant, id, sandbox == "1", note ?? string.Empty, ends, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Back(id, "keys", dp, error);
            }
            await audit.RecordAsync(a.Me.Address, "org.app.http-key", await NameAsync(svc, a.Tenant, id, ct).ConfigureAwait(false), Client(http), (sandbox == "1" ? "sandbox " : "live ") + fingerprint).ConfigureAwait(false);
            sessions.Reveal(SessionRegistry.SessionOf(http.User) ?? string.Empty, new[] { key! });
            return Results.Redirect(Page(id, "keys") + "&key=1");
        }).RequireAuthorization();

        app.MapPost("/org/apps/{id}/http-key/retire", async (HttpContext http, string id, [FromForm] string? key, MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OrgEndpoints.AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            string? error = await svc.RetireHttpKeyAsync(a.Tenant, id, key ?? string.Empty, ct).ConfigureAwait(false);
            if (error is null)
            {
                await audit.RecordAsync(a.Me.Address, "org.app.http-key-retired", await NameAsync(svc, a.Tenant, id, ct).ConfigureAwait(false), Client(http), key ?? string.Empty).ConfigureAwait(false);
            }
            return Back(id, "keys", dp, error);
        }).RequireAuthorization();

        app.MapPost("/org/apps/{id}/api-settings", async (HttpContext http, string id, [FromForm] string? senders, [FromForm] int perHour, [FromForm] int perDay, [FromForm] string? allowedFrom, [FromForm] string? signing,
            MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OrgEndpoints.AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            string? error = await svc.SaveAppApiSettingsAsync(a.Tenant, id, senders ?? string.Empty, perHour, perDay, allowedFrom ?? string.Empty, signing == "1", ct).ConfigureAwait(false);
            if (error is null)
            {
                await audit.RecordAsync(a.Me.Address, "org.app.api-settings", await NameAsync(svc, a.Tenant, id, ct).ConfigureAwait(false), Client(http),
                    string.Create(CultureInfo.InvariantCulture, $"{perHour} an hour, {perDay} a day; senders: {Flat(senders)}; allowed from: {Flat(allowedFrom)}")).ConfigureAwait(false);
            }
            return Back(id, "limits", dp, error, error is null ? "saved" : null);
        }).RequireAuthorization();

        app.MapPost("/org/apps/{id}/signing-required", async (HttpContext http, string id, [FromForm] string? on, MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OrgEndpoints.AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            string? error = await svc.ChangeAppAsync(a.Tenant.Id, id, x => { x.SigningRequired = on == "1"; return null; }, ct).ConfigureAwait(false);
            if (error is null)
            {
                await audit.RecordAsync(a.Me.Address, on == "1" ? "org.app.signing-required" : "org.app.signing-optional", await NameAsync(svc, a.Tenant, id, ct).ConfigureAwait(false), Client(http)).ConfigureAwait(false);
            }
            return Back(id, "signing", dp, error);
        }).RequireAuthorization();

        app.MapPost("/org/apps/{id}/signing", async (HttpContext http, string id, [FromForm] string? pem, MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OrgEndpoints.AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            string? error = await svc.AddSigningKeyAsync(a.Tenant, id, pem ?? string.Empty, ct).ConfigureAwait(false);
            if (error is null)
            {
                await audit.RecordAsync(a.Me.Address, "org.app.signing-key", await NameAsync(svc, a.Tenant, id, ct).ConfigureAwait(false), Client(http)).ConfigureAwait(false);
            }
            return Back(id, "signing", dp, error);
        }).RequireAuthorization();

        app.MapPost("/org/apps/{id}/signing/retire", async (HttpContext http, string id, [FromForm] string? key, MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OrgEndpoints.AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            string? error = await svc.RetireSigningKeyAsync(a.Tenant, id, key ?? string.Empty, ct).ConfigureAwait(false);
            if (error is null)
            {
                await audit.RecordAsync(a.Me.Address, "org.app.signing-key-retired", await NameAsync(svc, a.Tenant, id, ct).ConfigureAwait(false), Client(http), key ?? string.Empty).ConfigureAwait(false);
            }
            return Back(id, "signing", dp, error);
        }).RequireAuthorization();

        app.MapPost("/org/apps/{id}/encryption", async (HttpContext http, string id, [FromForm] string? pem, MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OrgEndpoints.AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            string? error = await svc.AddEncryptionKeyAsync(a.Tenant, id, pem ?? string.Empty, ct).ConfigureAwait(false);
            if (error is null)
            {
                await audit.RecordAsync(a.Me.Address, "org.app.encryption-key", await NameAsync(svc, a.Tenant, id, ct).ConfigureAwait(false), Client(http)).ConfigureAwait(false);
            }
            return Back(id, "encryption", dp, error);
        }).RequireAuthorization();

        app.MapPost("/org/apps/{id}/encryption/remove", async (HttpContext http, string id, [FromForm] string? fingerprint, MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OrgEndpoints.AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            string? error = await svc.RemoveEncryptionKeyAsync(a.Tenant, id, fingerprint ?? string.Empty, ct).ConfigureAwait(false);
            if (error is null)
            {
                await audit.RecordAsync(a.Me.Address, "org.app.encryption-key-removed", await NameAsync(svc, a.Tenant, id, ct).ConfigureAwait(false), Client(http), fingerprint ?? string.Empty).ConfigureAwait(false);
            }
            return Back(id, "encryption", dp, error);
        }).RequireAuthorization();

        app.MapPost("/org/apps/{id}/inbox", async (HttpContext http, string id, [FromForm] string? address, [FromForm] string? share, MailboxService svc, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OrgEndpoints.AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            (string? error, MailboxRow? holder) = await svc.AddInboxAddressAsync(a.Tenant, a.Me.Address, id, address ?? string.Empty, share == "1", ct).ConfigureAwait(false);
            if (holder is not null)
            {
                // D-227: an address someone holds is shared only once the administrator confirms.
                return Results.Redirect(Page(id, "inbox") + "&share=" + Uri.EscapeDataString((address ?? string.Empty).Trim().ToLowerInvariant()));
            }
            return Back(id, "inbox", dp, error);
        }).RequireAuthorization();

        app.MapPost("/org/apps/{id}/inbox/remove", async (HttpContext http, string id, [FromForm] string? address, MailboxService svc, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OrgEndpoints.AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            return Back(id, "inbox", dp, await svc.RemoveInboxAddressAsync(a.Tenant, a.Me.Address, id, address ?? string.Empty, ct).ConfigureAwait(false));
        }).RequireAuthorization();

        app.MapPost("/org/apps/{id}/inbox/quota", async (HttpContext http, string id, [FromForm] int gb, MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OrgEndpoints.AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            string? error = await svc.SetAppInboxQuotaAsync(a.Tenant, id, gb, ct).ConfigureAwait(false);
            if (error is null)
            {
                await audit.RecordAsync(a.Me.Address, "org.app.inbox-quota", await NameAsync(svc, a.Tenant, id, ct).ConfigureAwait(false), Client(http), gb.ToString(CultureInfo.InvariantCulture) + " GB").ConfigureAwait(false);
            }
            return Back(id, "inbox", dp, error);
        }).RequireAuthorization();

        app.MapPost("/org/apps/{id}/webhooks", async (HttpContext http, string id, [FromForm] string? url, MailboxService svc, SessionRegistry sessions, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OrgEndpoints.AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            // The ticked event types, read from the form itself (a string[] parameter does not bind on .NET 10).
            string[] events = http.Request.Form["events"].Where(e => !string.IsNullOrEmpty(e)).Select(e => e!).ToArray();
            (string? error, AppWebhook? hook) = await svc.AddWebhookAsync(a.Tenant, id, url ?? string.Empty, events, ct).ConfigureAwait(false);
            if (hook is null)
            {
                return Back(id, "webhooks", dp, error);
            }
            await audit.RecordAsync(a.Me.Address, "org.app.webhook", await NameAsync(svc, a.Tenant, id, ct).ConfigureAwait(false), Client(http), hook.Url).ConfigureAwait(false);
            sessions.Reveal(SessionRegistry.SessionOf(http.User) ?? string.Empty, new[] { hook.Secret });
            return Results.Redirect(Page(id, "webhooks") + "&key=1");
        }).RequireAuthorization();

        app.MapPost("/org/apps/{id}/webhooks/remove", async (HttpContext http, string id, [FromForm] string? hook, MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OrgEndpoints.AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            string? error = await svc.RemoveWebhookAsync(a.Tenant, id, hook ?? string.Empty, ct).ConfigureAwait(false);
            if (error is null)
            {
                await audit.RecordAsync(a.Me.Address, "org.app.webhook-removed", await NameAsync(svc, a.Tenant, id, ct).ConfigureAwait(false), Client(http), hook ?? string.Empty).ConfigureAwait(false);
            }
            return Back(id, "webhooks", dp, error);
        }).RequireAuthorization();

        // "Send again" (DES-14 §8): one delivery, now.
        app.MapPost("/org/apps/{id}/webhooks/resend", async (HttpContext http, string id, [FromForm] Guid delivery, MailboxService svc, IMessageStore store, AppWebhookWorker worker, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OrgEndpoints.AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            if (store is not IAppApiStore apps || await apps.GetAppWebhookDeliveryAsync(delivery, ct).ConfigureAwait(false) is not AppWebhookDelivery d || d.AppId != id
                || await svc.AppOfAsync(a.Tenant.Id, id, ct).ConfigureAwait(false) is null)
            {
                return Results.NotFound();
            }
            d.Status = "pending";
            await worker.DeliverAsync(apps, d, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            return Back(id, "webhooks", dp, null);
        }).RequireAuthorization();

        app.MapPost("/org/app-templates", async (HttpContext http, [FromForm] string? name, [FromForm] string? language, [FromForm] string? subject, [FromForm] string? text, [FromForm] string? html, [FromForm] string? @checked, [FromForm] string? back,
            MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OrgEndpoints.AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            string? error = await svc.SaveAppTemplateAsync(a.Tenant.Id, name ?? string.Empty, language ?? "en", new AppTemplateText { Subject = subject ?? string.Empty, Text = text ?? string.Empty, Html = html ?? string.Empty, Checked = @checked == "1" }, a.Me.Address, ct).ConfigureAwait(false);
            if (error is null)
            {
                await audit.RecordAsync(a.Me.Address, "org.app-template", name ?? string.Empty, Client(http), language ?? "en").ConfigureAwait(false);
            }
            return Results.Redirect(LocalPath.Safe(back) + (error is null ? string.Empty : "&e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error))));
        }).RequireAuthorization();

        app.MapPost("/org/app-templates/{action:regex(^(check|remove)$)}", async (HttpContext http, string action, [FromForm] string? name, [FromForm] string? language, [FromForm] string? back,
            MailboxService svc, AuditTrail audit, CancellationToken ct) =>
        {
            if (await OrgEndpoints.AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            bool done = action == "check"
                ? await svc.CheckAppTemplateLanguageAsync(a.Tenant.Id, name ?? string.Empty, language ?? string.Empty, ct).ConfigureAwait(false)
                : await svc.RemoveAppTemplateAsync(a.Tenant.Id, name ?? string.Empty, ct).ConfigureAwait(false);
            if (done)
            {
                await audit.RecordAsync(a.Me.Address, action == "check" ? "org.app-template-checked" : "org.app-template-removed", name ?? string.Empty, Client(http), language ?? string.Empty).ConfigureAwait(false);
            }
            return Results.Redirect(LocalPath.Safe(back));
        }).RequireAuthorization();

        // DES-14 §20 (D-226): the patient-data mark, set and removed by an operator only, with a reason.
        app.MapPost("/ops/orgs/{tenantId:guid}/apps/{id}/patient-data", async (HttpContext http, Guid tenantId, string id, [FromForm] string? on, [FromForm] string? reason,
            MailboxService svc, IMailboxStore boxes, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OpsEndpoints.OperatorAsync(http, svc, ct).ConfigureAwait(false) is not { } me || await boxes.GetTenantByIdAsync(tenantId, ct).ConfigureAwait(false) is not TenantRow tenant)
            {
                return Results.NotFound();
            }
            string? error = await svc.SetPatientDataAsync(tenant, id, on == "1", reason ?? string.Empty, me.Address, ct).ConfigureAwait(false);
            return Results.Redirect("/ops/orgs?sel=" + tenantId + (error is null ? "&saved=patient-data" : "&e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error))));
        }).RequireAuthorization();

        // DES-14 §9 point 4: private and loopback webhooks and Allowed from ranges, per organisation.
        app.MapPost("/ops/orgs/{tenantId:guid}/private-addresses", async (HttpContext http, Guid tenantId, [FromForm] string? on, MailboxService svc, AuditTrail audit, CancellationToken ct) =>
        {
            if (await OpsEndpoints.OperatorAsync(http, svc, ct).ConfigureAwait(false) is not { } me)
            {
                return Results.NotFound();
            }
            OpsRecord ops = await svc.ReadTenantDocumentAsync<OpsRecord>(tenantId, MailboxService.OpsKind, ct).ConfigureAwait(false) ?? new OpsRecord();
            ops.AllowPrivateAddresses = on == "1";
            await svc.WriteTenantDocumentAsync(tenantId, MailboxService.OpsKind, ops, ct).ConfigureAwait(false);
            await audit.RecordAsync(me.Address, on == "1" ? "ops.org.private-addresses-on" : "ops.org.private-addresses-off", tenantId.ToString(), Client(http)).ConfigureAwait(false);
            return Results.Redirect("/ops/orgs?sel=" + tenantId + "&saved=private");
        }).RequireAuthorization();
    }

    private static string Page(string id, string tab) => "/org/apps?sel=" + Uri.EscapeDataString(id) + "&api=" + tab;

    private static IResult Back(string id, string tab, IDataProtectionProvider dp, string? error, string? saved = null) =>
        Results.Redirect(Page(id, tab) + (error is not null ? "&e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error)) : saved is not null ? "&saved=" + saved : string.Empty));

    private static async Task<string> NameAsync(MailboxService svc, TenantRow tenant, string id, CancellationToken ct) =>
        (await svc.AppOfAsync(tenant.Id, id, ct).ConfigureAwait(false))?.Name ?? id;

    private static string Client(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static string Flat(string? text)
    {
        string t = (text ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' ').Trim();
        return t.Length == 0 ? "-" : t.Length > 200 ? t[..200] : t;
    }
}
