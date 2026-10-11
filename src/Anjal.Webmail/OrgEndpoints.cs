using System.Globalization;
using System.Text;
using Anjal.Store;
using Anjal.Webmail.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace Anjal.Webmail;

/// <summary>
/// rc.14: the organisation console's forms (boards OrgPeople to OrgBranding).
/// Only an administrator of the organisation reaches them; every change is
/// written to the activity log; none of them reads anyone's mail.
/// </summary>
internal static class OrgEndpoints
{
    /// <summary>The organisation document holding domains waiting for proof of ownership.</summary>
    internal const string PendingDomainsKind = "domains-pending";

    internal static void Map(WebApplication app)
    {
        // ---- people ----
        app.MapPost("/org/people/invite", async (HttpContext http, [FromForm] string? name, [FromForm] string? local, [FromForm] string? domain, [FromForm] string? personal, [FromForm] string? role,
            MailboxService svc, SessionRegistry sessions, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            string address = (local ?? string.Empty).Trim() + "@" + (domain ?? string.Empty).Trim();
            (string? error, string? token) = await svc.InviteAsync(a.Tenant, name ?? string.Empty, address, personal ?? string.Empty, role == "admin", a.Me.DisplayName.Length > 0 ? a.Me.DisplayName : a.Me.Address, BaseUrl(http), ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Results.Redirect("/org/people?invite=1&e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error)));
            }
            await audit.RecordAsync(a.Me.Address, "org.person.invited", address.ToLowerInvariant(), Client(http)).ConfigureAwait(false);
            sessions.Reveal(SessionRegistry.SessionOf(http.User) ?? string.Empty, new[] { address.ToLowerInvariant() + "|" + BaseUrl(http) + "/invite/" + token });
            return Results.Redirect("/org/people?links=1");
        }).RequireAuthorization();

        app.MapPost("/org/people/import", async (HttpContext http, IFormFile? list, MailboxService svc, SessionRegistry sessions, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            if (list is null || list.Length == 0 || list.Length > 1_000_000)
            {
                return Results.Redirect("/org/people?e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, "Choose a CSV file of up to 1 MB, with a Name and an Address column.")));
            }
            string csv;
            using (var reader = new StreamReader(list.OpenReadStream(), Encoding.UTF8))
            {
                csv = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }
            IReadOnlyList<(string Address, string? Token, string? Error)> results = await svc.ImportPeopleAsync(a.Tenant, csv, a.Me.DisplayName.Length > 0 ? a.Me.DisplayName : a.Me.Address, BaseUrl(http), ct).ConfigureAwait(false);
            await audit.RecordAsync(a.Me.Address, "org.person.imported", $"{results.Count(r => r.Token is not null)} of {results.Count}", Client(http)).ConfigureAwait(false);
            sessions.Reveal(SessionRegistry.SessionOf(http.User) ?? string.Empty,
                results.Select(r => r.Address + "|" + (r.Token is null ? "!" + r.Error : BaseUrl(http) + "/invite/" + r.Token)).ToList());
            return Results.Redirect("/org/people?links=1");
        }).RequireAuthorization();

        app.MapPost("/org/people/{id:guid}/{action}", async (HttpContext http, Guid id, string action, [FromForm] string? value,
            MailboxService svc, SessionRegistry sessions, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            MailboxRow? person = (await svc.GetMailboxAnyAsync(id, ct).ConfigureAwait(false)) is MailboxRow m && m.TenantId == a.Tenant.Id ? m : null;
            if (person is null)
            {
                return Results.NotFound();
            }
            string back = "/org/people?sel=" + id;
            string? error = null;
            string logged;
            switch (action)
            {
                case "reset":
                    string? token = await svc.AdminResetPasswordAsync(id, a.Me.DisplayName.Length > 0 ? a.Me.DisplayName : a.Me.Address, BaseUrl(http), ct).ConfigureAwait(false);
                    await sessions.EndOthersAsync(id, null, ct).ConfigureAwait(false);
                    sessions.Reveal(SessionRegistry.SessionOf(http.User) ?? string.Empty, new[] { person.Address + "|" + BaseUrl(http) + "/invite/" + token });
                    back = "/org/people?sel=" + id + "&links=1";
                    logged = "org.person.reset";
                    break;
                case "signout":
                    await sessions.EndOthersAsync(id, null, ct).ConfigureAwait(false);
                    await svc.ForgetTrustedDevicesAsync(id, ct).ConfigureAwait(false);
                    logged = "org.person.signedout";
                    break;
                case "disable":
                case "enable":
                    if (id == a.Me.Id)
                    {
                        error = "You cannot disable yourself.";
                        logged = string.Empty;
                        break;
                    }
                    await svc.SetEnabledAsync(id, action == "enable", ct).ConfigureAwait(false);
                    if (action == "disable")
                    {
                        await sessions.EndOthersAsync(id, null, ct).ConfigureAwait(false);
                    }
                    logged = "org.person." + action + "d";
                    break;
                case "role":
                    error = await svc.SetAdminAsync(a.Tenant, id, value == "admin", ct).ConfigureAwait(false);
                    logged = "org.person.role";
                    break;
                case "storage":
                    // DES-11 D2: a smaller limit for this person, or the plan's again (empty).
                    error = await svc.SetStorageCapAsync(a.Tenant, id, decimal.TryParse((value ?? string.Empty).Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out decimal gb) && gb > 0 && gb <= 1_000_000
                        ? (long)Math.Round(gb * Sizes.GB, MidpointRounding.AwayFromZero)
                        : 0, ct).ConfigureAwait(false);
                    logged = "org.person.storage";
                    break;
                case "merge":
                    // Owner, 8 Oct 2026: Send one each only for the people the organisation chooses.
                    error = await svc.SetMergeAccessAsync(a.Tenant, id, value == "on", ct).ConfigureAwait(false);
                    logged = value == "on" ? "org.person.merge.on" : "org.person.merge.off";
                    break;
                case "twostep-off":
                    await svc.AdminTurnOffTwoStepAsync(id, ct).ConfigureAwait(false);
                    logged = "org.person.twostepoff";
                    break;
                case "hold":
                    error = await svc.SetHoldAsync(a.Tenant, id, true, a.Me.DisplayName.Length > 0 ? a.Me.DisplayName : a.Me.Address, value ?? string.Empty, ct).ConfigureAwait(false);
                    back = "/org/retention";
                    logged = "org.hold.placed";
                    break;
                case "unhold":
                    error = await svc.SetHoldAsync(a.Tenant, id, false, a.Me.Address, string.Empty, ct).ConfigureAwait(false);
                    back = "/org/retention";
                    logged = "org.hold.lifted";
                    break;
                default:
                    return Results.NotFound();
            }
            if (error is not null)
            {
                return Results.Redirect(back + (back.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error)));
            }
            await audit.RecordAsync(a.Me.Address, logged, person.Address, Client(http), value ?? string.Empty).ConfigureAwait(false);
            return Results.Redirect(back + (back.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "saved=" + action);
        }).RequireAuthorization();

        app.MapPost("/org/retention/hold", async (HttpContext http, [FromForm] Guid mailbox, [FromForm] string? value, MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            string? error = await svc.SetHoldAsync(a.Tenant, mailbox, true, a.Me.DisplayName.Length > 0 ? a.Me.DisplayName : a.Me.Address, value ?? string.Empty, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Results.Redirect("/org/retention?hold=1&e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error)));
            }
            string held = (await svc.GetMailboxAnyAsync(mailbox, ct).ConfigureAwait(false))?.Address ?? mailbox.ToString();
            await audit.RecordAsync(a.Me.Address, "org.hold.placed", held, Client(http), value ?? string.Empty).ConfigureAwait(false);
            return Results.Redirect("/org/retention?saved=hold");
        }).RequireAuthorization();

        // ---- sign-in rules ----
        app.MapPost("/org/signin", async (HttpContext http, [FromForm] int minLength, [FromForm] int? history, [FromForm] string? twoStep, [FromForm] string? methods,
            [FromForm] int sharedIdle, [FromForm] int ownIdle, [FromForm] int stay, [FromForm] int trust, [FromForm] string? offline, MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            var policy = new SignInPolicy
            {
                MinLength = minLength,
                PasswordHistory = history ?? 0,
                // No longer offered or applied (owner, 10 Oct 2026, P3); a stored value is kept as it is.
                ExpiryDays = (await svc.SignInPolicyOfAsync(a.Tenant.Id, ct).ConfigureAwait(false)).ExpiryDays,
                TwoStep = twoStep ?? "optional",
                Methods = methods ?? "all",
                SharedIdleMinutes = sharedIdle,
                OwnIdleHours = ownIdle,
                StayDays = stay,
                TrustDays = trust,
                OfflineMail = offline != "0",
                OfflineSubjects = offline is not ("0" or "2"),
            };
            string? error = await svc.SaveSignInPolicyAsync(a.Tenant.Id, policy, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Results.Redirect("/org/signin?e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error)));
            }
            await audit.RecordAsync(a.Me.Address, "org.signin.rules", a.Tenant.Slug, Client(http),
                $"length {policy.MinLength}, earlier passwords {policy.PasswordHistory}, two-step {policy.TwoStep}, idle {policy.SharedIdleMinutes}m/{policy.OwnIdleHours}h").ConfigureAwait(false);
            return Results.Redirect("/org/signin?saved=1");
        }).RequireAuthorization();

        // ---- domains ----
        app.MapPost("/org/domains/add", async (HttpContext http, [FromForm] string? domain, MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            string d = (domain ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
            if (!IsDomainName(d))
            {
                return Results.Redirect("/org/domains?e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, "That is not a domain name.")));
            }
            if (await svc.TenantDomainAsync(d, ct).ConfigureAwait(false) is not null)
            {
                return Results.Redirect("/org/domains?e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, "That domain is already in use on this service.")));
            }
            Dictionary<string, string> pending = await svc.ReadTenantDocumentAsync<Dictionary<string, string>>(a.Tenant.Id, PendingDomainsKind, ct).ConfigureAwait(false) ?? new Dictionary<string, string>();
            if (!pending.ContainsKey(d))
            {
                pending[d] = "anjal-verify=" + SessionRegistry.NewId();
                await svc.WriteTenantDocumentAsync(a.Tenant.Id, PendingDomainsKind, pending, ct).ConfigureAwait(false);
            }
            await audit.RecordAsync(a.Me.Address, "org.domain.added", d, Client(http)).ConfigureAwait(false);
            return Results.Redirect("/org/domains?domain=" + Uri.EscapeDataString(d));
        }).RequireAuthorization();

        app.MapPost("/org/domains/prove", async (HttpContext http, [FromForm] string? domain, MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            string d = (domain ?? string.Empty).Trim().ToLowerInvariant();
            Dictionary<string, string> pending = await svc.ReadTenantDocumentAsync<Dictionary<string, string>>(a.Tenant.Id, PendingDomainsKind, ct).ConfigureAwait(false) ?? new Dictionary<string, string>();
            if (!pending.TryGetValue(d, out string? token))
            {
                return Results.Redirect("/org/domains");
            }
            IReadOnlyList<string>? found = await DnsLookup.TxtAsync("_anjal." + d, ct).ConfigureAwait(false);
            if (found is null)
            {
                return Results.Redirect("/org/domains?domain=" + Uri.EscapeDataString(d) + "&e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, "The DNS could not be asked just now. Try again in a minute.")));
            }
            if (!found.Any(t => string.Equals(t.Trim(), token, StringComparison.Ordinal)))
            {
                return Results.Redirect("/org/domains?domain=" + Uri.EscapeDataString(d) + "&e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, "The TXT record was not found yet. DNS changes can take up to an hour to be seen.")));
            }
            string? error = await svc.AddTenantDomainAsync(a.Tenant, d, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Results.Redirect("/org/domains?e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error)));
            }
            pending.Remove(d);
            await svc.WriteTenantDocumentAsync(a.Tenant.Id, PendingDomainsKind, pending, ct).ConfigureAwait(false);
            await audit.RecordAsync(a.Me.Address, "org.domain.proved", d, Client(http)).ConfigureAwait(false);
            return Results.Redirect("/org/domains?domain=" + Uri.EscapeDataString(d) + "&check=1&saved=proved");
        }).RequireAuthorization();

        app.MapGet("/org/domains/records.txt", async (HttpContext http, string? domain, MailboxService svc, CancellationToken ct) =>
        {
            if (await AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            var sb = new StringBuilder();
            sb.Append("; DNS records for Anjal, ").Append(a.Tenant.DisplayName.Length > 0 ? a.Tenant.DisplayName : a.Tenant.Slug).Append("\r\n");
            foreach (TenantDomainRow d in await svc.TenantDomainsAsync(a.Tenant.Id, ct).ConfigureAwait(false))
            {
                if (domain is not null && !string.Equals(domain, d.Domain, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                sb.Append("\r\n; ").Append(d.Domain).Append("\r\n");
                foreach (DnsRecordCheck r in await svc.DomainRecordsAsync(d.Domain, null, ct).ConfigureAwait(false))
                {
                    string name = r.Name == "@" ? d.Domain + "." : r.Name + "." + d.Domain + ".";
                    string value = r.Type == "TXT" ? "\"" + r.Value + "\"" : r.Value + ".";
                    sb.Append(CultureInfo.InvariantCulture, $"{name}\t3600\tIN\t{r.Type}\t{value}\r\n");
                }
            }
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/plain; charset=utf-8", "anjal-dns-records.txt");
        }).RequireAuthorization();

        // ---- shared mailboxes ----
        app.MapPost("/org/shared/new", async (HttpContext http, [FromForm] string? local, [FromForm] string? domain, [FromForm] string? name, MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            string address = (local ?? string.Empty).Trim() + "@" + (domain ?? string.Empty).Trim();
            (string? error, Guid? id) = await svc.CreateSharedMailboxAsync(a.Tenant, address, name ?? string.Empty, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Results.Redirect("/org/shared?new=1&e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error)));
            }
            await audit.RecordAsync(a.Me.Address, "org.shared.created", address.ToLowerInvariant(), Client(http)).ConfigureAwait(false);
            return Results.Redirect("/org/shared?sel=" + id);
        }).RequireAuthorization();

        app.MapPost("/org/shared/{id:guid}/member", async (HttpContext http, Guid id, [FromForm] Guid person, [FromForm] string? right, MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            string? error = await svc.SetSharedRightAsync(a.Tenant, id, person, right is null or "none" ? null : right, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Results.Redirect("/org/shared?sel=" + id + "&e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error)));
            }
            string who = (await svc.GetMailboxAnyAsync(person, ct).ConfigureAwait(false))?.Address ?? person.ToString();
            string box = (await svc.GetMailboxAnyAsync(id, ct).ConfigureAwait(false))?.Address ?? id.ToString();
            await audit.RecordAsync(a.Me.Address, "org.shared.rights", box + " - " + who, Client(http), right ?? "none").ConfigureAwait(false);
            return Results.Redirect("/org/shared?sel=" + id);
        }).RequireAuthorization();

        app.MapPost("/org/shared/{id:guid}/sending", async (HttpContext http, Guid id, [FromForm] string? mode, [FromForm] string? keep, MailboxService svc, AuditTrail audit, CancellationToken ct) =>
        {
            if (await AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            if (await svc.SetSharedSendingAsync(a.Tenant, id, mode ?? "as", keep == "1", ct).ConfigureAwait(false))
            {
                string box = (await svc.GetMailboxAnyAsync(id, ct).ConfigureAwait(false))?.Address ?? id.ToString();
                await audit.RecordAsync(a.Me.Address, "org.shared.sending", box, Client(http), (mode ?? "as") + (keep == "1" ? ", keeps sent mail" : ", no sent copy")).ConfigureAwait(false);
            }
            return Results.Redirect("/org/shared?sel=" + id);
        }).RequireAuthorization();

        // ---- retention ----
        app.MapPost("/org/retention", async (HttpContext http, [FromForm] int trash, [FromForm] int? trashmax, [FromForm] int junk, [FromForm] int outbox, [FromForm] int apps, [FromForm] int evidence, [FromForm] string? who,
            MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            // DES-11 D7: who gets a changed Trash or Junk setting; "everyone" never where it keeps mail longer.
            string whoGets = WhoGets.Read(who);
            RetentionPolicy before = await svc.ReadTenantDocumentAsync<RetentionPolicy>(a.Tenant.Id, MailboxService.RetentionKind, ct).ConfigureAwait(false) ?? new RetentionPolicy();
            string trashBefore = before.TrashDays.ToString(CultureInfo.InvariantCulture);
            string junkBefore = before.JunkDays.ToString(CultureInfo.InvariantCulture);
            string trashAfter = trash.ToString(CultureInfo.InvariantCulture);
            string junkAfter = junk.ToString(CultureInfo.InvariantCulture);
            if (whoGets == WhoGets.Everyone && (MailboxService.WouldLoosen("trash", trashBefore, trashAfter) || MailboxService.WouldLoosen("junk", junkBefore, junkAfter)))
            {
                return Results.Redirect("/org/retention?e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, "Keeping mail longer is never given to everyone: people who chose a shorter time keep it.")));
            }
            var p = new RetentionPolicy { TrashDays = trash, TrashMaxDays = trashmax ?? MailboxService.MaxTrashDays, JunkDays = junk, OutboxDays = outbox, AppCopyDays = apps, EvidenceYears = evidence };
            string? error = await svc.SaveRetentionAsync(a.Tenant, p, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Results.Redirect("/org/retention?e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error)));
            }
            int moved = await svc.ApplyDefaultChangeAsync(a.Tenant, "trash", trashBefore, trashAfter, whoGets, ct).ConfigureAwait(false)
                + await svc.ApplyDefaultChangeAsync(a.Tenant, "junk", junkBefore, junkAfter, whoGets, ct).ConfigureAwait(false);
            string reach = trashBefore != trashAfter || junkBefore != junkAfter ? $"; given to {WhoGets.Words(whoGets)} ({moved} own settings changed)" : string.Empty;
            await audit.RecordAsync(a.Me.Address, "org.retention", a.Tenant.Slug, Client(http), $"trash {trash} (people may choose up to {p.TrashMaxDays}), junk {junk}, outbox {outbox}, apps {apps}, evidence {evidence}y{reach}").ConfigureAwait(false);
            return Results.Redirect("/org/retention?saved=1");
        }).RequireAuthorization();

        // ---- sending (rc.15, item 64) ----
        app.MapPost("/org/sending", async (HttpContext http, [FromForm] int? limit, MailboxService svc, AuditTrail audit, CancellationToken ct) =>
        {
            if (await AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            int kept = await svc.SaveMergeLimitAsync(a.Tenant.Id, limit ?? MailboxService.DefaultDailyMergeLimit, ct).ConfigureAwait(false);
            await audit.RecordAsync(a.Me.Address, "org.merge.limit", a.Tenant.Slug, Client(http), kept.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
            return Results.Redirect("/org/sending?saved=1");
        }).RequireAuthorization();

        // rc.15 (item 60): figures per person, on with a purpose and a start, or off; everyone is told either way.
        app.MapPost("/org/figures", async (HttpContext http, [FromForm] string? action, [FromForm] string? purpose, [FromForm] string? from, MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            bool on = action == "on";
            string? error = await svc.SetPersonFiguresAsync(a.Tenant, a.Me, on, purpose, from, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Results.Redirect("/org/figures?e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error)));
            }
            PersonFiguresSetting now = await svc.PersonFiguresOfAsync(a.Tenant.Id, ct).ConfigureAwait(false);
            await audit.RecordAsync(a.Me.Address, on ? "org.figures.on" : "org.figures.off", a.Tenant.Slug, Client(http),
                on ? "from " + now.From + "; purpose: " + now.Purpose : string.Empty).ConfigureAwait(false);
            return Results.Redirect("/org/figures?saved=1");
        }).RequireAuthorization();

        // ---- applications ----
        app.MapPost("/org/apps/new", async (HttpContext http, [FromForm] string? name, [FromForm] string? description, [FromForm] string? sendsAs, [FromForm] string? receives,
            MailboxService svc, SessionRegistry sessions, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            (string? error, string? appId, string? user, string? secret) = await svc.AddAppAsync(a.Tenant, name ?? string.Empty, description ?? string.Empty, sendsAs ?? string.Empty, receives ?? string.Empty, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Results.Redirect("/org/apps?new=1&e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error)));
            }
            await audit.RecordAsync(a.Me.Address, "org.app.added", name ?? string.Empty, Client(http), sendsAs ?? string.Empty).ConfigureAwait(false);
            sessions.Reveal(SessionRegistry.SessionOf(http.User) ?? string.Empty, new[] { user + "|" + secret });
            return Results.Redirect("/org/apps?sel=" + appId + "&key=1");
        }).RequireAuthorization();

        app.MapPost("/org/apps/{id}/key", async (HttpContext http, string id, [FromForm] string? rotate, MailboxService svc, SessionRegistry sessions, AuditTrail audit, CancellationToken ct) =>
        {
            if (await AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            (string Username, string Secret)? key = await svc.NewKeyAsync(a.Tenant, id, rotate == "1", ct).ConfigureAwait(false);
            if (key is null)
            {
                return Results.NotFound();
            }
            await audit.RecordAsync(a.Me.Address, rotate == "1" ? "org.app.rotated" : "org.app.key", AppName(await svc.AppsOfAsync(a.Tenant.Id, ct).ConfigureAwait(false), id), Client(http)).ConfigureAwait(false);
            sessions.Reveal(SessionRegistry.SessionOf(http.User) ?? string.Empty, new[] { key.Value.Username + "|" + key.Value.Secret });
            return Results.Redirect("/org/apps?sel=" + id + "&key=1");
        }).RequireAuthorization();

        app.MapPost("/org/apps/{id}/revoke", async (HttpContext http, string id, [FromForm] string? key, MailboxService svc, AuditTrail audit, CancellationToken ct) =>
        {
            if (await AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            string name = AppName(await svc.AppsOfAsync(a.Tenant.Id, ct).ConfigureAwait(false), id);
            if (await svc.RevokeKeyAsync(a.Tenant, id, key ?? string.Empty, ct).ConfigureAwait(false))
            {
                await audit.RecordAsync(a.Me.Address, "org.app.revoked", name, Client(http)).ConfigureAwait(false);
            }
            return Results.Redirect("/org/apps?sel=" + id);
        }).RequireAuthorization();

        // ---- activity log ----
        app.MapGet("/org/log.csv", async (HttpContext http, string? who, string? kind, int? days, string? q, MailboxService svc, IMessageStore store, CancellationToken ct) =>
        {
            if (await AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            IReadOnlyList<AuditEvent> events = await OrgLog.ForAsync(a.Tenant, svc, store, who, kind, days ?? 7, q, ct).ConfigureAwait(false);
            var sb = new StringBuilder("time,who,action,on,from,result,detail,entry,chain\r\n");
            foreach (AuditEvent e in events)
            {
                sb.Append(string.Join(",", new[]
                {
                    e.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture), e.Actor, OrgLog.Describe(e.Action), e.Subject, e.RemoteAddress,
                    OrgLog.Refused(e.Action) ? "Refused" : "Done", e.Detail, e.Seq.ToString(CultureInfo.InvariantCulture), e.Chain,
                }.Select(MailboxService.CsvCell))).Append("\r\n");
            }
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", "anjal-activity-log.csv");
        }).RequireAuthorization();

        app.MapPost("/org/log/verify", async (HttpContext http, MailboxService svc, IMessageStore store, AuditTrail audit, CancellationToken ct) =>
        {
            if (await AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            AuditChainCheck check = await store.VerifyAuditChainAsync(ct).ConfigureAwait(false);
            await audit.RecordAsync(a.Me.Address, "org.log.verified", check.Intact ? "intact" : "broken at " + check.BrokenAt, Client(http)).ConfigureAwait(false);
            return Results.Redirect("/org/log?chain=" + (check.Intact ? "ok" : "broken") + "&n=" + check.Checked.ToString(CultureInfo.InvariantCulture) + (check.BrokenAt is long at ? "&at=" + at.ToString(CultureInfo.InvariantCulture) : string.Empty));
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        // ---- branding ----
        app.MapPost("/org/branding", async (HttpContext http, [FromForm] string? title, [FromForm] string? theme, [FromForm] string? language, [FromForm] string? clock, [FromForm] string? message, [FromForm] string? removeLogo, [FromForm] string? who, IFormFile? logo,
            MailboxService svc, Words words, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (await AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            Branding b = await svc.ReadTenantDocumentAsync<Branding>(a.Tenant.Id, MailboxService.BrandingKind, ct).ConfigureAwait(false) ?? new Branding();
            if (logo is not null && logo.Length > 0)
            {
                (string? uri, string? problem) = await LogoData(logo, ct).ConfigureAwait(false);
                if (problem is not null)
                {
                    return Results.Redirect("/org/branding?e=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, problem)));
                }
                b.Logo = uri!;
            }
            else if (removeLogo == "1")
            {
                b.Logo = string.Empty;
            }
            string themeBefore = b.DefaultTheme;
            string languageBefore = b.DefaultLanguage;
            string clockBefore = b.DefaultClock;
            b.Title = (title ?? string.Empty).Trim().Length > 80 ? title!.Trim()[..80] : (title ?? string.Empty).Trim();
            b.DefaultTheme = MailboxRow.ThemeColours.Contains(theme ?? string.Empty) ? theme! : "anjal";
            b.DefaultLanguage = words.IsEnabled(language) ? language! : "en";
            b.DefaultClock = Clocks.Choice(clock) == Clocks.Twelve ? Clocks.Twelve : Clocks.TwentyFour;
            b.Message = (message ?? string.Empty).Trim().Length > 140 ? message!.Trim()[..140] : (message ?? string.Empty).Trim();
            await svc.WriteTenantDocumentAsync(a.Tenant.Id, MailboxService.BrandingKind, b, ct).ConfigureAwait(false);
            // DES-11 D7: who gets a changed colour or clock, as the administrator answered.
            string whoGets = WhoGets.Read(who);
            if (whoGets == WhoGets.Joining && clockBefore != b.DefaultClock)
            {
                await svc.ApplyDefaultChangeAsync(a.Tenant, "clock", clockBefore, b.DefaultClock, whoGets, ct).ConfigureAwait(false);
            }
            Clocks.SetDefault(a.Tenant.Slug, b.DefaultClock);
            int moved = await svc.ApplyDefaultChangeAsync(a.Tenant, "theme", themeBefore, b.DefaultTheme, whoGets, ct).ConfigureAwait(false)
                + await svc.ApplyDefaultChangeAsync(a.Tenant, "language", languageBefore, b.DefaultLanguage, whoGets, ct).ConfigureAwait(false);
            if (whoGets != WhoGets.Joining)
            {
                moved += await svc.ApplyDefaultChangeAsync(a.Tenant, "clock", clockBefore, b.DefaultClock, whoGets, ct).ConfigureAwait(false);
            }
            string reach = themeBefore != b.DefaultTheme || clockBefore != b.DefaultClock || languageBefore != b.DefaultLanguage
                ? $"colour {themeBefore} to {b.DefaultTheme}, language {languageBefore} to {b.DefaultLanguage}, clock {clockBefore} to {b.DefaultClock}; given to {WhoGets.Words(whoGets)} ({moved} people changed)"
                : string.Empty;
            await audit.RecordAsync(a.Me.Address, "org.branding", a.Tenant.Slug, Client(http), reach).ConfigureAwait(false);
            return Results.Redirect("/org/branding?saved=1");
        }).RequireAuthorization();

        app.MapPost("/org/templates/delete", async (HttpContext http, [FromForm] string? key, MailboxService svc, AuditTrail audit, CancellationToken ct) =>
        {
            if (await AdminAsync(http, svc, ct).ConfigureAwait(false) is not { } a)
            {
                return Results.NotFound();
            }
            if (await svc.DeleteOrgTemplateAsync(a.Tenant.Id, key ?? string.Empty, ct).ConfigureAwait(false))
            {
                await audit.RecordAsync(a.Me.Address, "org.template.removed", key ?? string.Empty, Client(http)).ConfigureAwait(false);
            }
            return Results.Redirect("/org/branding?saved=template");
        }).RequireAuthorization();
    }

    /// <summary>The signed-in person and their organisation, when they administer it.</summary>
    /// <param name="http">The request.</param>
    /// <param name="svc">The mailbox service.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The organisation and the administrator, or null.</returns>
    internal static async Task<(TenantRow Tenant, MailboxRow Me)?> AdminAsync(HttpContext http, MailboxService svc, CancellationToken ct)
    {
        if (WebmailAuthService.PersonIdOf(http.User) is not Guid person || !await svc.IsOrgAdminAsync(person, ct).ConfigureAwait(false))
        {
            return null;
        }
        (TenantRow Tenant, MailboxRow Mailbox)? context = await svc.GetContextAsync(person, ct).ConfigureAwait(false);
        return context is null ? null : (context.Value.Tenant, context.Value.Mailbox);
    }

    /// <summary>The webmail's own address, for links in mail.</summary>
    /// <param name="http">The request.</param>
    /// <returns>For example https://mail.example.in.</returns>
    internal static string BaseUrl(HttpContext http) => http.Request.Scheme + "://" + http.Request.Host.Value;

    /// <summary>A name that can be a domain.</summary>
    /// <param name="d">The candidate.</param>
    /// <returns>Whether it can be.</returns>
    internal static bool IsDomainName(string d) => OrgEndpointsShared.IsDomainName(d);

    private static string AppName(IReadOnlyList<OrgApp> apps, string id) => apps.FirstOrDefault(x => x.Id == id)?.Name ?? id;

    private static string Client(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    // A logo: PNG, JPEG, WebP or SVG, up to 256 KB, kept as a data address. An
    // SVG is shown only as an image, where it can run nothing.
    private static async Task<(string? Uri, string? Problem)> LogoData(IFormFile file, CancellationToken ct)
    {
        if (file.Length > 256 * 1024)
        {
            return (null, "The logo must be 256 KB or smaller.");
        }
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct).ConfigureAwait(false);
        byte[] b = ms.ToArray();
        string? type = b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 ? "image/png"
            : b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "image/jpeg"
            : b.Length > 12 && Encoding.ASCII.GetString(b, 0, 4) == "RIFF" && Encoding.ASCII.GetString(b, 8, 4) == "WEBP" ? "image/webp"
            : Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 512)).Contains("<svg", StringComparison.OrdinalIgnoreCase) ? "image/svg+xml"
            : null;
        return type is null ? (null, "The logo must be a PNG, JPEG, WebP or SVG picture.") : ("data:" + type + ";base64," + Convert.ToBase64String(b), null);
    }
}

/// <summary>
/// The organisation's activity log (rc.14, board OrgLog): the audit trail's
/// entries that concern the organisation - its people's sign-ins and every
/// administrator action - described in words.
/// </summary>
internal static class OrgLog
{
    private static readonly Dictionary<string, string> Words = new(StringComparer.Ordinal)
    {
        ["webmail.signin"] = "Signed in",
        ["webmail.signin.failed"] = "Sign-in refused",
        ["webmail.signin.new-network"] = "Signed in from a new network",
        ["webmail.signin.throttled"] = "Sign-in refused: too many tries",
        ["webmail.signin.second-step-failed"] = "Sign-in refused at the second step",
        ["webmail.signin.passkey-failed"] = "Passkey refused",
        ["webmail.signin.password-accepted"] = "Password accepted, second step asked",
        ["webmail.signout"] = "Signed out",
        ["webmail.signout.idle"] = "Signed out for being idle",
        ["webmail.password.changed"] = "Changed password",
        ["webmail.password.change-refused"] = "Password change refused",
        ["webmail.reset.code-sent"] = "Reset code sent",
        ["webmail.reset.done"] = "Reset password",
        ["webmail.reset.refused"] = "Reset refused",
        ["webmail.recovery.changed"] = "Changed recovery address",
        ["webmail.twostep.on"] = "Turned on two-step",
        ["webmail.twostep.authenticator-removed"] = "Removed the authenticator",
        ["webmail.twostep.passkey-added"] = "Added a passkey",
        ["webmail.twostep.passkey-removed"] = "Removed a passkey",
        ["webmail.twostep.backup-codes-made"] = "Made backup codes",
        ["webmail.session.ended"] = "Signed a device out",
        ["webmail.session.ended-others"] = "Signed out everywhere else",
        ["webmail.approval.given"] = "Approved another device",
        ["webmail.approval.refused"] = "Refused another device",
        ["webmail.invitation.accepted"] = "Accepted the invitation",
        ["webmail.shared.opened"] = "Opened a shared mailbox",
        ["webmail.offline.on"] = "Turned on offline mail",
        ["webmail.offline.off"] = "Turned off offline mail",
        ["org.person.invited"] = "Invited a person",
        ["org.person.imported"] = "Invited people from a list",
        ["org.person.reset"] = "Reset password",
        ["org.person.signedout"] = "Signed out all devices",
        ["org.person.disabled"] = "Disabled a person",
        ["org.person.enabled"] = "Enabled a person",
        ["org.person.role"] = "Changed role",
        ["org.person.twostepoff"] = "Turned off two-step",
        ["org.signin.rules"] = "Changed sign-in rules",
        ["org.domain.added"] = "Added a domain",
        ["org.domain.proved"] = "Proved a domain",
        ["org.shared.created"] = "Made a shared mailbox",
        ["org.shared.rights"] = "Changed rights",
        ["org.shared.sending"] = "Changed how a shared mailbox sends",
        ["org.retention"] = "Changed retention",
        ["org.merge.limit"] = "Changed the daily limit for Send one each",
        ["org.figures.on"] = "Turned on figures per person",
        ["org.figures.off"] = "Turned off figures per person",
        ["org.person.merge.on"] = "Allowed Send one each",
        ["org.person.storage"] = "Changed storage limit",
        ["org.person.merge.off"] = "Stopped Send one each",
        ["org.hold.placed"] = "Placed a legal hold",
        ["org.hold.lifted"] = "Lifted a legal hold",
        ["org.app.added"] = "Added an application",
        ["org.app.key"] = "Made a key",
        ["org.app.rotated"] = "Rotated a key",
        ["org.app.revoked"] = "Revoked a key",
        ["org.branding"] = "Changed branding",
        ["org.template.added"] = "Added a template",
        ["org.template.removed"] = "Removed a template",
        ["org.log.verified"] = "Checked the log's chain",
    };

    /// <summary>An action in words.</summary>
    /// <param name="action">The code, for example webmail.signin.failed.</param>
    /// <returns>For example "Sign-in refused".</returns>
    internal static string Describe(string action) => Words.TryGetValue(action, out string? w) ? w : action;

    /// <summary>True for an action that was refused.</summary>
    /// <param name="action">The code.</param>
    /// <returns>Whether to show "Refused".</returns>
    internal static bool Refused(string action) =>
        action.EndsWith(".failed", StringComparison.Ordinal) || action.EndsWith(".throttled", StringComparison.Ordinal)
        || action.EndsWith("-failed", StringComparison.Ordinal) || action.EndsWith(".refused", StringComparison.Ordinal) || action.EndsWith("-refused", StringComparison.Ordinal);

    /// <summary>"sign-in", "admin" or "account": which kind of action.</summary>
    /// <param name="action">The code.</param>
    /// <returns>The kind.</returns>
    internal static string Kind(string action) =>
        action.StartsWith("org.", StringComparison.Ordinal) ? "admin"
        : action.StartsWith("webmail.signin", StringComparison.Ordinal) || action.StartsWith("webmail.signout", StringComparison.Ordinal) ? "sign-in"
        : "account";

    /// <summary>The entries concerning an organisation, newest first, filtered.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="svc">The mailbox service.</param>
    /// <param name="store">The audit trail.</param>
    /// <param name="who">Only this actor, or null.</param>
    /// <param name="kind">Only "sign-in", "admin" or "account", or null.</param>
    /// <param name="days">How many days back.</param>
    /// <param name="q">Words to find, or null.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The entries.</returns>
    internal static async Task<IReadOnlyList<AuditEvent>> ForAsync(TenantRow tenant, MailboxService svc, IMessageStore store, string? who, string? kind, int days, string? q, CancellationToken ct)
    {
        IReadOnlyList<TenantDomainRow> domains = await svc.TenantDomainsAsync(tenant.Id, ct).ConfigureAwait(false);
        string[] suffixes = domains.Select(d => "@" + d.Domain.ToLowerInvariant()).ToArray();
        bool Ours(string s) => s.Length > 0 && (suffixes.Any(x => s.Contains(x, StringComparison.OrdinalIgnoreCase)) || string.Equals(s, tenant.Slug, StringComparison.OrdinalIgnoreCase));
        DateTimeOffset since = DateTimeOffset.UtcNow - TimeSpan.FromDays(Math.Clamp(days, 1, 3650));
        var result = new List<AuditEvent>();
        DateTimeOffset? before = null;
        for (int page = 0; page < 20; page++)
        {
            IReadOnlyList<AuditEvent> batch = await store.ListAuditAsync(1000, before, ct).ConfigureAwait(false);
            foreach (AuditEvent e in batch)
            {
                if (e.At < since)
                {
                    return result;
                }
                if (!(Ours(e.Actor) || Ours(e.Subject)) || !e.Action.StartsWith("webmail.", StringComparison.Ordinal) && !e.Action.StartsWith("org.", StringComparison.Ordinal))
                {
                    continue;
                }
                if (who is { Length: > 0 } && !string.Equals(e.Actor, who, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (kind is { Length: > 0 } && Kind(e.Action) != kind)
                {
                    continue;
                }
                if (q is { Length: > 0 } && !(e.Actor + " " + Describe(e.Action) + " " + e.Subject + " " + e.Detail).Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                result.Add(e);
                if (result.Count >= 2000)
                {
                    return result;
                }
            }
            if (batch.Count < 1000)
            {
                break;
            }
            before = batch[^1].At;
        }
        return result;
    }
}

/// <summary>DNS lookups for the organisation console (rc.14), through the system's resolver; null when the DNS cannot be asked.</summary>
internal static class DnsLookup
{
    /// <summary>TXT records at a name, or MX hosts for a name written "MX:name".</summary>
    /// <param name="name">The name.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The records; empty when there are none; null when the DNS could not be asked.</returns>
    internal static async Task<IReadOnlyList<string>?> TxtAsync(string name, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            Anjal.Dns.DnsResolver resolver = Anjal.Dns.DnsResolver.CreateFromSystem();
            if (name.StartsWith("MX:", StringComparison.Ordinal))
            {
                IReadOnlyList<Anjal.Dns.MxRecord> mx = await resolver.ResolveMxAsync(name[3..], timeout.Token).ConfigureAwait(false);
                return mx.Select(m => m.Exchange).ToList();
            }
            return await resolver.LookupTxtAsync(name, timeout.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A lookup that cannot be made shows as not checked; it never breaks the page.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
#pragma warning restore CA1031
    }
}
