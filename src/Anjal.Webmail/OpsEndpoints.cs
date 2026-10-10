using System.Globalization;
using Anjal.Mailbox;
using Anjal.Store;
using Anjal.Webmail.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace Anjal.Webmail;

/// <summary>
/// rc.14: the Anjal console's forms (boards OpsOrgs, OpsLimits, OpsHealth).
/// Only the operators named in ANJAL_OPERATORS reach them; every action is
/// written to the activity log; none of them reads anyone's mail.
/// </summary>
internal static class OpsEndpoints
{
    internal static void Map(WebApplication app)
    {
        app.MapPost("/ops/orgs/new", async (HttpContext http, [FromForm] string? name, [FromForm] string? domain, [FromForm] string? adminName, [FromForm] string? adminLocal,
            [FromForm] string? personal, [FromForm] string? status, MailboxService svc, SessionRegistry sessions, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OperatorAsync(http, svc, ct).ConfigureAwait(false) is not { } me)
            {
                return Results.NotFound();
            }
            string d = (domain ?? string.Empty).Trim().ToLowerInvariant();
            (string? error, TenantRow? tenant, string? token) = await svc.CreateOrganisationAsync(name ?? string.Empty, d, adminName ?? string.Empty, (adminLocal ?? string.Empty).Trim() + "@" + d,
                personal ?? string.Empty, status ?? "setting-up", me.DisplayName.Length > 0 ? me.DisplayName : me.Address, OrgEndpoints.BaseUrl(http), ct).ConfigureAwait(false);
            if (tenant is null)
            {
                return Results.Redirect("/ops/orgs?new=1&e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error ?? "The organisation could not be added.")));
            }
            await audit.RecordAsync(me.Address, "ops.org.added", tenant.Slug, Client(http), d).ConfigureAwait(false);
            if (token is not null)
            {
                sessions.Reveal(SessionRegistry.SessionOf(http.User) ?? string.Empty, new[] { (adminLocal ?? string.Empty).Trim().ToLowerInvariant() + "@" + d + "|" + OrgEndpoints.BaseUrl(http) + "/invite/" + token });
            }
            return Results.Redirect("/ops/orgs?sel=" + tenant.Id + (token is not null ? "&links=1" : string.Empty) + (error is not null ? "&e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error)) : string.Empty));
        }).RequireAuthorization();

        app.MapPost("/ops/orgs/{id:guid}/limits", async (HttpContext http, Guid id, [FromForm] string? status, [FromForm] int people, [FromForm] string? plan, [FromForm] string? eachGb, [FromForm] string? sharedGb, [FromForm] int send, [FromForm] string? agreement,
            MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OperatorAsync(http, svc, ct).ConfigureAwait(false) is not { } me)
            {
                return Results.NotFound();
            }
            OpsRecord current = await svc.ReadTenantDocumentAsync<OpsRecord>(id, MailboxService.OpsKind, ct).ConfigureAwait(false) ?? new OpsRecord();
            var record = new OpsRecord
            {
                Status = status ?? current.Status,
                PeopleLimit = people,
                StorageGb = current.StorageGb,
                SendPerDay = send,
                AgreementSigned = agreement == "1" ? current.AgreementSigned ?? DateTimeOffset.UtcNow : null,
            };
            string? error = await svc.SaveOpsRecordAsync(id, record, ct).ConfigureAwait(false);
            // DES-11 D2 (owner, 10 Oct 2026): the storage plan, applied at once to every mailbox.
            long each = Bytes(eachGb);
            long sharedBytes = Bytes(sharedGb);
            error ??= await svc.SaveStoragePlanAsync(id, plan ?? "none", each, sharedBytes, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Results.Redirect("/ops/orgs?sel=" + id + "&edit=1&e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error)));
            }
            await audit.RecordAsync(me.Address, "ops.org.limits", id.ToString(), Client(http),
                string.Create(CultureInfo.InvariantCulture, $"{record.Status}; {record.PeopleLimit} people; storage {plan}: {Sizes.Text(each)} each, {Sizes.Text(sharedBytes)} shared; {record.SendPerDay} a day")).ConfigureAwait(false);
            return Results.Redirect("/ops/orgs?sel=" + id + "&saved=limits");
        }).RequireAuthorization();

        app.MapPost("/ops/orgs/{id:guid}/{action:regex(^(suspend|resume)$)}", async (HttpContext http, Guid id, string action, MailboxService svc, AuditTrail audit, CancellationToken ct) =>
        {
            if (await OperatorAsync(http, svc, ct).ConfigureAwait(false) is not { } me)
            {
                return Results.NotFound();
            }
            if (me.TenantId == id && action == "suspend")
            {
                return Results.Redirect("/ops/orgs?sel=" + id + "&saved=self");
            }
            TenantRow? t = await svc.SetSuspendedAsync(id, action == "suspend", ct).ConfigureAwait(false);
            if (t is null)
            {
                return Results.NotFound();
            }
            await audit.RecordAsync(me.Address, "ops.org." + (action == "suspend" ? "suspended" : "resumed"), t.Slug, Client(http)).ConfigureAwait(false);
            return Results.Redirect("/ops/orgs?sel=" + id + "&saved=" + action);
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery); // DES-11 F2: it binds no form field, so the framework did not check it

        app.MapPost("/ops/orgs/{id:guid}/message", async (HttpContext http, Guid id, [FromForm] string? subject, [FromForm] string? text, MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await OperatorAsync(http, svc, ct).ConfigureAwait(false) is not { } me)
            {
                return Results.NotFound();
            }
            OrgSummary? org = (await svc.ListOrganisationsAsync(ct).ConfigureAwait(false)).FirstOrDefault(o => o.Tenant.Id == id);
            string body = (text ?? string.Empty).Trim();
            if (org is null || org.Admins.Count == 0 || body.Length == 0)
            {
                return Results.Redirect("/ops/orgs?sel=" + id + "&e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, org is { Admins.Count: 0 } ? "This organisation has no administrator yet." : "Write the message first.")));
            }
            string subj = (subject ?? string.Empty).Trim();
            foreach (string admin in org.Admins)
            {
                await svc.SendSystemMailAsync(me, admin, subj.Length > 0 ? subj : "A message from the Anjal operator", body + "\r\n\r\n" + (me.DisplayName.Length > 0 ? me.DisplayName : me.Address) + ", Anjal operator", ct).ConfigureAwait(false);
            }
            await audit.RecordAsync(me.Address, "ops.org.messaged", org.Tenant.Slug, Client(http), subj).ConfigureAwait(false);
            return Results.Redirect("/ops/orgs?sel=" + id + "&saved=message");
        }).RequireAuthorization();

        app.MapPost("/ops/health/summary", async (HttpContext http, MailboxService svc, IMaildirStore maildir, AuditTrail audit, CancellationToken ct) =>
        {
            if (await OperatorAsync(http, svc, ct).ConfigureAwait(false) is not { } me)
            {
                return Results.NotFound();
            }
            (IReadOnlyList<HealthTile> tiles, IReadOnlyList<(DateTimeOffset Hour, long Count)> hours) = await svc.HealthAsync(RootOf(maildir), DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            string text = MailboxService.SummaryText(tiles, hours, await svc.ListOrganisationsAsync(ct).ConfigureAwait(false));
            string list = Environment.GetEnvironmentVariable("ANJAL_OPERATORS") ?? string.Empty;
            foreach (string op in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                await svc.SendSystemMailAsync(me, op, "Anjal service summary", text, ct).ConfigureAwait(false);
            }
            await audit.RecordAsync(me.Address, "ops.summary.sent", "operators", Client(http)).ConfigureAwait(false);
            return Results.Redirect("/ops/health?saved=summary");
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        // rc.15 (item 61; owner, 7 Oct 2026): a restore drill is recorded here; the next is due six months after the last that passed.
        app.MapPost("/ops/drill", async (HttpContext http, [FromForm] string? date, [FromForm] string? result, [FromForm] string? note, MailboxService svc, AuditTrail audit, CancellationToken ct) =>
        {
            if (await OperatorAsync(http, svc, ct).ConfigureAwait(false) is not { } me)
            {
                return Results.NotFound();
            }
            if (!DateTime.TryParseExact(date ?? string.Empty, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime day)
                || day > DateTime.UtcNow.Date.AddDays(1) || day < DateTime.UtcNow.Date.AddYears(-5))
            {
                return Results.Redirect("/ops/health?saved=drill-date");
            }
            string text = (note ?? string.Empty).Trim();
            var drill = new RestoreDrill
            {
                Date = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                Passed = result == "passed",
                Note = text.Length > 300 ? text[..300] : text,
                By = me.Address,
                RecordedAt = DateTimeOffset.UtcNow,
            };
            await svc.RecordDrillAsync(drill, ct).ConfigureAwait(false);
            await audit.RecordAsync(me.Address, "ops.drill.recorded", drill.Date, Client(http), (drill.Passed ? "passed" : "failed") + (drill.Note.Length > 0 ? "; " + drill.Note : string.Empty)).ConfigureAwait(false);
            return Results.Redirect("/ops/health?saved=drill");
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);
    }

    /// <summary>The signed-in person's own mailbox, when they are an operator.</summary>
    /// <param name="http">The request.</param>
    /// <param name="svc">The mailbox service.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Their mailbox, or null.</returns>
    internal static async Task<MailboxRow?> OperatorAsync(HttpContext http, MailboxService svc, CancellationToken ct)
    {
        if (WebmailAuthService.PersonIdOf(http.User) is not Guid person)
        {
            return null;
        }
        MailboxRow? me = await svc.GetMailboxAnyAsync(person, ct).ConfigureAwait(false);
        return me is not null && Operators.Is(me.Address) ? me : null;
    }

    /// <summary>Where mail is kept, for the disk tile.</summary>
    /// <param name="maildir">The store.</param>
    /// <returns>Its folder.</returns>
    // A size typed in GB (two decimals at most) as bytes; 0 when empty or not a number.
    private static long Bytes(string? gb) =>
        decimal.TryParse((gb ?? string.Empty).Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal g) && g > 0 && g <= 1_000_000
            ? (long)Math.Round(g * Sizes.GB, MidpointRounding.AwayFromZero)
            : 0;

    internal static string RootOf(IMaildirStore maildir) => (maildir as MaildirStore)?.Root ?? MaildirStore.DefaultRoot;

    private static string Client(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
