using System.Security.Claims;
using Anjal.Store;
using Anjal.Webmail.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace Anjal.Webmail;

/// <summary>
/// rc.13: signing in and out, and everything around it - "This computer is"
/// shared or the person's own, the second step (authenticator code, backup
/// code, passkey, or approval from another signed-in device), trusted
/// devices, forgotten passwords, invitations, the idle check, signed-in
/// devices, and the Security settings. Every form carries the page's
/// antiforgery token; anything sensitive is written to the audit trail.
/// </summary>
internal static class AuthEndpoints
{
    /// <summary>The cookie naming a sign-in that waits for its second step.</summary>
    internal const string PendingCookie = "anjal.pending";

    /// <summary>The cookie of a device trusted to skip the second step.</summary>
    internal const string TrustCookie = "anjal.trust";

    /// <summary>Set when a request's session was found idle or ended, for the sign-in redirect.</summary>
    internal const string EndedItem = "anjal.ended";

    private const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    internal static void Map(WebApplication app)
    {
        app.MapPost("/auth/login", async (HttpContext http, [FromForm] string address, [FromForm] string password, [FromForm] string? device,
            WebmailAuthService auth, MailboxService svc, LoginThrottle throttle, AuditTrail audit, CancellationToken ct) =>
        {
            string client = ClientOf(http);
            OrganisationIdentity? org = await svc.OrganisationForHostAsync(http.Request.Host.Host, ct).ConfigureAwait(false);
            string account = MailboxService.FullAddress(address, org?.Domain);
            string typed = (address ?? string.Empty).Trim();
            if (!throttle.IsPairAllowed(client, account))
            {
                // Refused before the password is checked: guessing costs the
                // attacker the wait, and costs this server nothing.
                await audit.RecordAsync("anonymous", "webmail.signin.throttled", account, client).ConfigureAwait(false);
                return Results.Redirect("/sign-in?throttled=1&u=" + Uri.EscapeDataString(typed));
            }
            // Owner, 10 Oct 2026 (P6): ten wrong passwords in a row pause signing in with the password
            // for 30 minutes - kept with the person, so it holds on every device and after a restart.
            // An address that does not exist answers the same way, from the throttle's own count.
            MailboxRow? named = await svc.FindMailboxAsync(typed, org?.Domain, ct).ConfigureAwait(false);
            if (!throttle.IsAccountAllowed(account) || (named is not null && await svc.PasswordPausedAsync(named.Id, DateTimeOffset.UtcNow, ct).ConfigureAwait(false)))
            {
                Anjal.Smtp.Pbkdf2Hasher.VerifyAgainstDummy(password ?? string.Empty);
                await audit.RecordAsync("anonymous", "webmail.signin.paused", account, client).ConfigureAwait(false);
                return Results.Redirect("/sign-in?paused=1&u=" + Uri.EscapeDataString(typed));
            }
            ClaimsPrincipal? principal = await auth.AuthenticateAsync(account, password ?? string.Empty, ct).ConfigureAwait(false);
            Guid? mailboxId = WebmailAuthService.PersonIdOf(principal);
            if (principal is null || mailboxId is null)
            {
                throttle.RecordFailure(client, account);
                await audit.RecordAsync("anonymous", "webmail.signin.failed", account, client).ConfigureAwait(false);
                if (named is not null && await svc.PasswordRefusedAsync(named.Id, DateTimeOffset.UtcNow, ct).ConfigureAwait(false) is not null)
                {
                    await audit.RecordAsync("anonymous", "webmail.signin.password-paused", account, client).ConfigureAwait(false);
                    await svc.SecurityMailAsync(named.Id, "Signing in with your password is paused for 30 minutes",
                        "Someone typed a wrong password for " + named.Address + " ten times in a row. Signing in is paused for 30 minutes on every device; \"Forgot password\" still works. Your password was not guessed; if this was not you, nothing more is needed.",
                        client, http.Request.Headers.UserAgent.ToString(), Array.Empty<string>(), ct).ConfigureAwait(false);
                    return Results.Redirect("/sign-in?paused=1&u=" + Uri.EscapeDataString(typed));
                }
                return Results.Redirect("/sign-in?error=1&u=" + Uri.EscapeDataString(typed) + (device == "shared" ? "&shared=1" : string.Empty));
            }
            throttle.RecordSuccess(client, account);
            // Owner, 10 Oct 2026: a password shorter than the rules now ask for this person is
            // replaced before anything else (the "password" demand), and the wrong count starts again.
            if (await svc.GetMailboxAnyAsync(mailboxId.Value, ct).ConfigureAwait(false) is MailboxRow signedIn)
            {
                await svc.PasswordAcceptedAsync(signedIn, password ?? string.Empty, ct).ConfigureAwait(false);
            }
            bool shared = device == "shared";
            SecurityDocument security = await svc.GetSecurityAsync(mailboxId.Value, ct).ConfigureAwait(false);
            bool trusted = !shared && await svc.IsTrustedDeviceAsync(mailboxId.Value, http.Request.Cookies[TrustCookie], ct).ConfigureAwait(false);
            if (security.TwoStepOn && !trusted && security.SecondStepLockedUntil is DateTimeOffset lockedUntil && lockedUntil > DateTimeOffset.UtcNow)
            {
                // DES-11 S3: locked on every device until the fifteen minutes are over.
                await audit.RecordAsync(account, "webmail.signin.second-step-locked", account, client).ConfigureAwait(false);
                return Results.Redirect("/sign-in?locked=1&u=" + Uri.EscapeDataString(typed));
            }
            if (security.TwoStepOn && !trusted)
            {
                SessionRegistry sessions = http.RequestServices.GetRequiredService<SessionRegistry>();
                string pending = sessions.AddPending(mailboxId.Value, account, shared);
                http.Response.Cookies.Append(PendingCookie, pending, ShortCookie(http, TimeSpan.FromMinutes(10)));
                await audit.RecordAsync(account, "webmail.signin.password-accepted", account, client).ConfigureAwait(false);
                return Results.Redirect("/sign-in/two-step");
            }
            return Results.Redirect(await CompleteAsync(http, mailboxId.Value, shared, trusted ? "trusted device" : "password", ct).ConfigureAwait(false) ?? "/sign-in?error=1");
        });

        app.MapPost("/sign-out", async (HttpContext http, IAntiforgery antiforgery, AuditTrail audit, SessionRegistry sessions) =>
        {
            await antiforgery.ValidateRequestAsync(http).ConfigureAwait(false);
            IFormCollection form = await http.Request.ReadFormAsync().ConfigureAwait(false);
            bool idle = form["reason"] == "idle";
            string who = http.User.Identity?.Name ?? string.Empty;
            string tz = WebmailAuthService.ClockOf(http.User).ZoneId;
            if (who.Length > 0)
            {
                await audit.RecordAsync(who, idle ? "webmail.signout.idle" : "webmail.signout", who, ClientOf(http)).ConfigureAwait(false);
            }
            if (WebmailAuthService.PersonIdOf(http.User) is Guid mailboxId && SessionRegistry.SessionOf(http.User) is string sid)
            {
                await sessions.EndAsync(mailboxId, sid).ConfigureAwait(false);
            }
            await http.SignOutAsync(Scheme).ConfigureAwait(false);
            // "Leaves nothing behind": the browser drops this site's cache and storage.
            http.Response.Headers["Clear-Site-Data"] = "\"cache\", \"storage\"";
            return Results.Redirect(SignedOutUrl(idle, tz));
        });

        // ---- the second step ----
        app.MapPost("/auth/two-step", async (HttpContext http, [FromForm] string? code, [FromForm] string? method, [FromForm] string? trust,
            MailboxService svc, SessionRegistry sessions, AuditTrail audit, CancellationToken ct) =>
        {
            PendingSignIn? pending = sessions.Pending(http.Request.Cookies[PendingCookie]);
            if (pending is null)
            {
                return Results.Redirect("/sign-in?expired=1");
            }
            if (await svc.SecondStepLockedAsync(pending.MailboxId, DateTimeOffset.UtcNow, ct).ConfigureAwait(false))
            {
                sessions.RemovePending(pending.Id);
                return Results.Redirect("/sign-in?locked=1");
            }
            bool backup = method == "backup";
            bool ok = backup
                ? await svc.UseBackupCodeAsync(pending.MailboxId, code ?? string.Empty, ct).ConfigureAwait(false)
                : await svc.CheckAuthenticatorCodeAsync(pending.MailboxId, code ?? string.Empty, ct).ConfigureAwait(false);
            if (!ok)
            {
                pending.Attempts++;
                await audit.RecordAsync(pending.Address, "webmail.signin.second-step-failed", pending.Address, ClientOf(http)).ConfigureAwait(false);
                if (pending.Attempts >= 5)
                {
                    await LockAsync(http, svc, sessions, audit, pending, ct).ConfigureAwait(false);
                    return Results.Redirect("/sign-in?locked=1");
                }
                return Results.Redirect("/sign-in/two-step?error=1" + (backup ? "&method=backup" : string.Empty));
            }
            return Results.Redirect(await FinishSecondStepAsync(http, pending, trust == "1", backup ? "backup code" : "authenticator", ct).ConfigureAwait(false));
        });

        app.MapPost("/auth/two-step/ask", (HttpContext http, SessionRegistry sessions) =>
        {
            PendingSignIn? pending = sessions.Pending(http.Request.Cookies[PendingCookie]);
            if (pending is null)
            {
                return Results.Redirect("/sign-in?expired=1");
            }
            Approval approval = sessions.RequestApproval(pending.MailboxId, "sign-in", http.Request.Headers.UserAgent.ToString(), ClientOf(http));
            pending.ApprovalId = approval.Id;
            return Results.Redirect("/sign-in/two-step?waiting=1");
        }).AddEndpointFilter(Program.RequireAntiforgery);

        app.MapGet("/auth/two-step/status", (HttpContext http, SessionRegistry sessions) =>
        {
            PendingSignIn? pending = sessions.Pending(http.Request.Cookies[PendingCookie]);
            Approval? approval = sessions.ApprovalOf(pending?.ApprovalId);
            string state = pending is null || approval is null ? "expired" : approval.Answer switch { true => "approved", false => "refused", _ => "waiting" };
            return Results.Json(new { state });
        });

        app.MapPost("/auth/two-step/approved", async (HttpContext http, SessionRegistry sessions, CancellationToken ct) =>
        {
            PendingSignIn? pending = sessions.Pending(http.Request.Cookies[PendingCookie]);
            Approval? approval = sessions.ApprovalOf(pending?.ApprovalId);
            if (pending is null || approval is null || approval.Answer != true || approval.MailboxId != pending.MailboxId || approval.Used)
            {
                return Results.Redirect("/sign-in/two-step?refused=1");
            }
            approval.Used = true;
            return Results.Redirect(await FinishSecondStepAsync(http, pending, false, "approval from another device", ct).ConfigureAwait(false));
        }).AddEndpointFilter(Program.RequireAntiforgery);

        app.MapPost("/auth/passkey/options", async (HttpContext http, [FromForm] string? purpose, MailboxService svc, SessionRegistry sessions, CancellationToken ct) =>
        {
            PendingSignIn? pending = sessions.Pending(http.Request.Cookies[PendingCookie]);
            if (pending is null)
            {
                return Results.Json(new { error = "Your sign-in had expired. Start again." }, statusCode: 400);
            }
            SecurityDocument security = await svc.GetSecurityAsync(pending.MailboxId, ct).ConfigureAwait(false);
            if (security.Passkeys.Count == 0)
            {
                return Results.Json(new { error = "No passkey has been added to this account yet." }, statusCode: 400);
            }
            (string id, byte[] challenge) = sessions.NewChallenge(pending.MailboxId, "sign-in");
            return Results.Json(new
            {
                challengeId = id,
                challenge = WebAuthn.ToBase64Url(challenge),
                rpId = http.Request.Host.Host,
                allow = security.Passkeys.Select(p => p.Id).ToArray(),
            });
        });

        app.MapPost("/auth/passkey/verify", async (HttpContext http, [FromForm] string? challengeId, [FromForm] string? id, [FromForm] string? clientDataJSON,
            [FromForm] string? authenticatorData, [FromForm] string? signature, MailboxService svc, SessionRegistry sessions, AuditTrail audit, CancellationToken ct) =>
        {
            PendingSignIn? pending = sessions.Pending(http.Request.Cookies[PendingCookie]);
            if (pending is null)
            {
                return Results.Json(new { error = "Your sign-in had expired. Start again." }, statusCode: 400);
            }
            byte[]? challenge = sessions.TakeChallenge(challengeId, pending.MailboxId, "sign-in");
            SecurityDocument security = await svc.GetSecurityAsync(pending.MailboxId, ct).ConfigureAwait(false);
            PasskeyRecord? passkey = security.Passkeys.Find(p => p.Id == id);
            if (challenge is null || passkey is null)
            {
                pending.Attempts++;
                return Results.Json(new { error = challenge is null ? "The request had expired. Try again." : "That passkey is not one of this account's." }, statusCode: 400);
            }
            (uint count, string? error) = WebAuthn.Verify(passkey, clientDataJSON ?? string.Empty, authenticatorData ?? string.Empty, signature ?? string.Empty, challenge, OriginOf(http), http.Request.Host.Host);
            if (error is not null)
            {
                pending.Attempts++;
                await audit.RecordAsync(pending.Address, "webmail.signin.passkey-failed", pending.Address, ClientOf(http)).ConfigureAwait(false);
                if (pending.Attempts >= 5)
                {
                    await LockAsync(http, svc, sessions, audit, pending, ct).ConfigureAwait(false);
                    return Results.Json(new { error = "Too many wrong tries. The second step is locked for fifteen minutes; you have been sent a mail about it.", next = "/sign-in?locked=1" }, statusCode: 400);
                }
                return Results.Json(new { error }, statusCode: 400);
            }
            await svc.PasskeyUsedAsync(pending.MailboxId, passkey.Id, count, ct).ConfigureAwait(false);
            return Results.Json(new { next = await FinishSecondStepAsync(http, pending, false, "passkey", ct).ConfigureAwait(false) });
        });

        // ---- forgotten password ----
        app.MapPost("/auth/forgot", async (HttpContext http, [FromForm] string? user, MailboxService svc, LoginThrottle throttle, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            OrganisationIdentity? org = await svc.OrganisationForHostAsync(http.Request.Host.Host, ct).ConfigureAwait(false);
            string account = MailboxService.FullAddress(user, org?.Domain);
            string client = ClientOf(http);
            string typed = Uri.EscapeDataString((user ?? string.Empty).Trim());
            if (!throttle.IsAllowed(client, "reset:" + account))
            {
                return Results.Redirect($"/sign-in/forgot?u={typed}&throttled=1");
            }
            throttle.RecordFailure(client, "reset:" + account);
            MailboxRow? mailbox = await svc.FindMailboxAsync(account, org?.Domain, ct).ConfigureAwait(false);
            // DES-11 S1 (owner, 10 Oct 2026, "A"): every user name gets the same answer and the same
            // page - an account that does not exist, one switched off, and one without a recovery
            // address alike - so this page never tells a stranger which user names exist.
            if (mailbox is null || !mailbox.Enabled)
            {
                Guid tenant = org?.TenantId ?? await svc.TenantOfDomainAsync(account[(account.IndexOf('@', StringComparison.Ordinal) + 1)..], ct).ConfigureAwait(false);
                string decoy = ResetTicket(dp).Protect("d" + Guid.NewGuid().ToString("N") + "|" + tenant.ToString("N") + "|" + account, TimeSpan.FromMinutes(30));
                await audit.RecordAsync(account, "webmail.reset.unknown", account, client).ConfigureAwait(false);
                // About as long as sending a real code takes, so the answer's speed tells nothing either.
                await Task.Delay(System.Security.Cryptography.RandomNumberGenerator.GetInt32(120, 450), ct).ConfigureAwait(false);
                return Results.Redirect($"/sign-in/reset?t={Uri.EscapeDataString(decoy)}");
            }
            string ticket = ResetTicket(dp).Protect(mailbox.Id.ToString("N"), TimeSpan.FromMinutes(30));
            string? error = await svc.SendResetCodeAsync(mailbox, ct).ConfigureAwait(false);
            await audit.RecordAsync(account, error is null ? "webmail.reset.code-sent" : "webmail.reset.code-not-sent", account, client).ConfigureAwait(false);
            return Results.Redirect($"/sign-in/reset?t={Uri.EscapeDataString(ticket)}");
        });

        app.MapPost("/auth/reset", async (HttpContext http, [FromForm] string? t, [FromForm] string? code, [FromForm] string? password, [FromForm] string? method, [FromForm] string? approval,
            MailboxService svc, SessionRegistry sessions, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            MailboxRow? mailbox = await MailboxOfTicketAsync(dp, svc, t, ct).ConfigureAwait(false);
            if (mailbox is null)
            {
                return Results.Redirect("/sign-in/forgot?expired=1");
            }
            string back = "/sign-in/reset?t=" + Uri.EscapeDataString(t!) + (method == "backup" ? "&method=backup" : string.Empty);
            string? error;
            if (IsDecoy(mailbox))
            {
                // DES-11 S1: an account that does not exist answers as a wrong code would.
                SignInPolicy decoyPolicy = await svc.SignInPolicyOfAsync(mailbox.TenantId, ct).ConfigureAwait(false);
                error = await Anjal.Smtp.PasswordPolicy.CheckAsync(password ?? string.Empty, mailbox.Address, string.Empty, MailboxService.ResetLength(decoyPolicy), ct).ConfigureAwait(false)
                    ?? (method == "approval" ? null : method == "backup" ? "That backup code is not right, or it has been used." : "That code is not right. Check the message and try again.");
                if (method == "approval")
                {
                    return Results.Redirect("/sign-in/reset?t=" + Uri.EscapeDataString(t!) + "&refused=1");
                }
                await audit.RecordAsync(mailbox.Address, "webmail.reset.refused", mailbox.Address, ClientOf(http)).ConfigureAwait(false);
                return Results.Redirect(back + "&e=" + Uri.EscapeDataString(Note(dp, error!)));
            }
            if (method == "approval")
            {
                Approval? a = sessions.ApprovalOf(approval);
                if (a is null || a.MailboxId != mailbox.Id || a.Kind != "reset" || a.Answer != true || a.Used)
                {
                    return Results.Redirect("/sign-in/reset?t=" + Uri.EscapeDataString(t!) + "&refused=1");
                }
                error = await svc.SetPasswordAsync(mailbox, password ?? string.Empty, MailboxService.ResetLength(await svc.SignInPolicyOfAsync(mailbox.TenantId, ct).ConfigureAwait(false)), ct).ConfigureAwait(false);
                if (error is null)
                {
                    a.Used = true;
                }
                back = "/sign-in/reset?t=" + Uri.EscapeDataString(t!) + "&waiting=" + Uri.EscapeDataString(approval!);
            }
            else
            {
                error = await svc.ResetPasswordAsync(mailbox, code ?? string.Empty, password ?? string.Empty, method == "backup", ct).ConfigureAwait(false);
            }
            if (error is not null)
            {
                await audit.RecordAsync(mailbox.Address, "webmail.reset.refused", mailbox.Address, ClientOf(http)).ConfigureAwait(false);
                return Results.Redirect(back + "&e=" + Uri.EscapeDataString(Note(dp, error)));
            }
            // Every device is signed out: whoever knew the old password is out too.
            await sessions.EndOthersAsync(mailbox.Id, null, ct).ConfigureAwait(false);
            await audit.RecordAsync(mailbox.Address, "webmail.reset.done", mailbox.Address, ClientOf(http)).ConfigureAwait(false);
            // DES-11 S2: told by mail; the sign-in page says it too (reset=1).
            await svc.SecurityMailAsync(mailbox.Id, "Your password was reset",
                "The password of " + mailbox.Address + " was reset with \"Forgot your password?\". Every device was signed out, and every trusted device will ask for the second step once more.",
                ClientOf(http), http.Request.Headers.UserAgent.ToString(), Array.Empty<string>(), ct).ConfigureAwait(false);
            return Results.Redirect("/sign-in?reset=1&u=" + Uri.EscapeDataString(mailbox.LocalPart));
        });

        app.MapPost("/auth/reset/resend", async (HttpContext http, [FromForm] string? t, MailboxService svc, IDataProtectionProvider dp, LoginThrottle throttle, CancellationToken ct) =>
        {
            MailboxRow? mailbox = await MailboxOfTicketAsync(dp, svc, t, ct).ConfigureAwait(false);
            if (mailbox is null)
            {
                return Results.Redirect("/sign-in/forgot?expired=1");
            }
            string client = ClientOf(http);
            if (!throttle.IsAllowed(client, "reset:" + mailbox.Address))
            {
                return Results.Redirect("/sign-in/reset?t=" + Uri.EscapeDataString(t!) + "&e=" + Uri.EscapeDataString(Note(dp, "Too many codes have been asked for. Wait fifteen minutes.")));
            }
            throttle.RecordFailure(client, "reset:" + mailbox.Address);
            // DES-11 S1: no recovery address, or no account, looks the same as a code sent.
            string? error = IsDecoy(mailbox) ? null : await svc.SendResetCodeAsync(mailbox, ct).ConfigureAwait(false);
            if (error == "This account has no recovery address.")
            {
                error = null;
            }
            return Results.Redirect("/sign-in/reset?t=" + Uri.EscapeDataString(t!) + (error is null ? "&sent=1" : "&e=" + Uri.EscapeDataString(Note(dp, error))));
        });

        app.MapPost("/auth/reset/ask", async (HttpContext http, [FromForm] string? t, MailboxService svc, SessionRegistry sessions, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            MailboxRow? mailbox = await MailboxOfTicketAsync(dp, svc, t, ct).ConfigureAwait(false);
            if (mailbox is null)
            {
                return Results.Redirect("/sign-in/forgot?expired=1");
            }
            Approval a = sessions.RequestApproval(mailbox.Id, "reset", http.Request.Headers.UserAgent.ToString(), ClientOf(http));
            return Results.Redirect("/sign-in/reset?t=" + Uri.EscapeDataString(t!) + "&waiting=" + Uri.EscapeDataString(a.Id));
        });

        app.MapGet("/auth/reset/status", async (HttpContext http, string? t, string? a, MailboxService svc, SessionRegistry sessions, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            MailboxRow? mailbox = await MailboxOfTicketAsync(dp, svc, t, ct).ConfigureAwait(false);
            Approval? approval = sessions.ApprovalOf(a);
            string state = mailbox is null || approval is null || approval.MailboxId != mailbox.Id ? "expired"
                : approval.Answer switch { true => "approved", false => "refused", _ => "waiting" };
            return Results.Json(new { state });
        });

        // ---- invitations ----
        app.MapPost("/auth/invite", async (HttpContext http, [FromForm] string? token, [FromForm] string? password, [FromForm] string? recovery,
            MailboxService svc, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            (MailboxRow Mailbox, InvitationState Invitation, string Organisation)? found = await svc.FindInvitationAsync(token, ct).ConfigureAwait(false);
            if (found is null)
            {
                return Results.Redirect("/invite/expired");
            }
            string? error = await svc.AcceptInvitationAsync(token!, password ?? string.Empty, recovery ?? string.Empty, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Results.Redirect("/invite/" + Uri.EscapeDataString(token!) + "?e=" + Uri.EscapeDataString(Note(dp, error)));
            }
            await audit.RecordAsync(found.Value.Mailbox.Address, "webmail.invitation.accepted", found.Value.Mailbox.Address, ClientOf(http)).ConfigureAwait(false);
            string? next = await CompleteAsync(http, found.Value.Mailbox.Id, false, "invitation", ct).ConfigureAwait(false);
            return Results.Redirect(next is null ? "/sign-in" : "/settings/security/two-step?welcome=1");
        });

        // ---- the password rules, ticked as the person types ----
        app.MapPost("/auth/password-check", async (HttpContext http, [FromForm] string? password, [FromForm] string? t, [FromForm] string? token,
            MailboxService svc, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            MailboxRow? mailbox = null;
            int least = Anjal.Smtp.PasswordPolicy.AloneLength;
            if (WebmailAuthService.PersonIdOf(http.User) is Guid signedIn)
            {
                mailbox = (await svc.GetContextAsync(signedIn, ct).ConfigureAwait(false))?.Mailbox;
                least = mailbox is null ? least : await svc.PasswordLengthForAsync(mailbox, null, ct).ConfigureAwait(false);
            }
            else if (!string.IsNullOrEmpty(t))
            {
                mailbox = await MailboxOfTicketAsync(dp, svc, t, ct).ConfigureAwait(false);
                least = mailbox is null ? least : MailboxService.ResetLength(await svc.SignInPolicyOfAsync(mailbox.TenantId, ct).ConfigureAwait(false));
            }
            else if (!string.IsNullOrEmpty(token))
            {
                mailbox = (await svc.FindInvitationAsync(token, ct).ConfigureAwait(false))?.Mailbox;
                least = mailbox is null ? least : await svc.PasswordLengthForAsync(mailbox, null, ct).ConfigureAwait(false);
            }
            Anjal.Smtp.PasswordRuleResults r = Anjal.Smtp.PasswordPolicy.Evaluate(password ?? string.Empty, mailbox?.Address, mailbox?.DisplayName, least);
            return Results.Json(new { length = r.LongEnough, common = r.NotCommon, personal = r.NotPersonal, least });
        });

        // The language chips on the sign-in pages: only a language that is switched on.
        app.MapGet("/auth/language", (HttpContext http, string? l, string? back, Words words) =>
        {
            if (words.IsEnabled(l))
            {
                http.Response.Cookies.Append(AuthWords.Cookie, l!, new CookieOptions { HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(365), IsEssential = true });
            }
            return Results.Redirect(LocalPath.Safe(back));
        });

        // ---- while signed in: the idle check and approvals ----
        app.MapGet("/auth/alive", (HttpContext http, SessionRegistry sessions) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            int? left = sessions.SecondsLeft(SessionRegistry.SessionOf(http.User));
            if (mailboxId is null || left is null)
            {
                return Results.Json(new { left = 0, approvals = Array.Empty<object>() });
            }
            return Results.Json(new
            {
                left = left.Value,
                shared = SessionRegistry.IsShared(http.User),
                approvals = sessions.Waiting(mailboxId.Value).Select(a => new { id = a.Id, kind = a.Kind, device = a.Device, address = a.Address }).ToArray(),
            });
        });

        app.MapPost("/auth/alive", (HttpContext http, SessionRegistry sessions) =>
            Results.Json(new { left = sessions.SecondsLeft(SessionRegistry.SessionOf(http.User)) ?? 0 }))
            .RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        app.MapPost("/auth/approvals/{id}", async (HttpContext http, string id, [FromForm] string? answer, SessionRegistry sessions, AuditTrail audit) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            bool approve = answer == "approve";
            bool done = sessions.Answer(mailboxId.Value, id, approve);
            string who = http.User.Identity?.Name ?? string.Empty;
            if (done)
            {
                await audit.RecordAsync(who, approve ? "webmail.approval.given" : "webmail.approval.refused", who, ClientOf(http)).ConfigureAwait(false);
            }
            return http.Request.Headers.XRequestedWith == "anjal" ? Results.Json(new { done }) : Results.Redirect("/folder/INBOX");
        }).RequireAuthorization();

        MapSecuritySettings(app);

        // ---- shared mailboxes (rc.14) ----
        app.MapPost("/shared/open", async (HttpContext http, [FromForm] Guid id, MailboxService svc, AuditTrail audit, CancellationToken ct) =>
        {
            if (WebmailAuthService.PersonIdOf(http.User) is not Guid person)
            {
                return Results.Redirect("/sign-in");
            }
            if (id == person)
            {
                await ResignAsync(http, WebmailAuthService.WithActing(http.User, null)).ConfigureAwait(false);
                return Results.Redirect("/folder/INBOX");
            }
            if (await svc.SharedRightAsync(person, id, ct).ConfigureAwait(false) is null)
            {
                return Results.Redirect("/folder/INBOX?sharedclosed=1");
            }
            await ResignAsync(http, WebmailAuthService.WithActing(http.User, id)).ConfigureAwait(false);
            string who = http.User.Identity?.Name ?? string.Empty;
            string box = (await svc.GetContextAsync(id, ct).ConfigureAwait(false))?.Mailbox.Address ?? id.ToString();
            await audit.RecordAsync(who, "webmail.shared.opened", box, ClientOf(http)).ConfigureAwait(false);
            return Results.Redirect("/folder/INBOX");
        }).RequireAuthorization();

        app.MapPost("/shared/close", async (HttpContext http) =>
        {
            await ResignAsync(http, WebmailAuthService.WithActing(http.User, null)).ConfigureAwait(false);
            return Results.Redirect("/folder/INBOX");
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);
    }

    /// <summary>
    /// Whether a request is the person doing something (it resets the idle
    /// clock) rather than the page checking in. Polls, look-ups and the idle
    /// check itself do not count; the page reports typing and clicking with a
    /// POST to /auth/alive.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>True for activity.</returns>
    internal static bool IsActivity(HttpRequest request)
    {
        string path = request.Path.Value ?? string.Empty;
        if (HttpMethods.IsPost(request.Method))
        {
            return true;
        }
        return !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase)
            && request.Headers.XRequestedWith.Count == 0;
    }

    /// <summary>
    /// Re-issue the cookie with changed claims (a theme, a shared mailbox
    /// opened), keeping how long it lasts: a sign-in on a person's own device
    /// stays persistent, a shared computer's stays a session cookie.
    /// </summary>
    /// <param name="http">The request.</param>
    /// <param name="principal">The changed principal.</param>
    /// <returns>A task.</returns>
    internal static async Task ResignAsync(HttpContext http, ClaimsPrincipal principal)
    {
        AuthenticateResult current = await http.AuthenticateAsync(Scheme).ConfigureAwait(false);
        AuthenticationProperties properties = current.Properties ?? new AuthenticationProperties();
        await http.SignInAsync(Scheme, principal, properties).ConfigureAwait(false);
    }

    /// <summary>
    /// rc.14: what comes before anything else for a signed-in request - a
    /// shared mailbox that may no longer be opened, a read-only shared mailbox
    /// asked to change something, and what the organisation demands first (a
    /// new password, two-step sign-in).
    /// </summary>
    /// <param name="http">The request.</param>
    /// <param name="next">The rest of the pipeline.</param>
    /// <returns>A task.</returns>
    internal static async Task DemandsAsync(HttpContext http, Func<Task> next)
    {
        if (http.User.Identity?.IsAuthenticated != true || WebmailAuthService.PersonIdOf(http.User) is not Guid person)
        {
            await next().ConfigureAwait(false);
            return;
        }
        string path = http.Request.Path.Value ?? string.Empty;
        bool post = HttpMethods.IsPost(http.Request.Method);
        bool page = !post && Path.GetExtension(path).Length == 0 && http.Request.Headers.XRequestedWith.Count == 0 && !path.StartsWith("/api/", StringComparison.Ordinal);
        MailboxService svc = http.RequestServices.GetRequiredService<MailboxService>();
        if (WebmailAuthService.ActingOf(http.User) is Guid shared)
        {
            string? right = await svc.SharedRightCachedAsync(person, shared, http.RequestAborted).ConfigureAwait(false);
            if (right is null)
            {
                await ResignAsync(http, WebmailAuthService.WithActing(http.User, null)).ConfigureAwait(false);
                http.Response.Redirect("/folder/INBOX?sharedclosed=1");
                return;
            }
            if (right == "read" && post && !ReadOnlyAllows(path))
            {
                http.Response.Redirect("/folder/INBOX?readonly=1");
                return;
            }
        }
        if ((page || post) && !DemandAllows(path))
        {
            string? demand = await svc.SignInDemandCachedAsync(person, http.RequestAborted).ConfigureAwait(false);
            if (demand == "password" && !path.StartsWith("/settings/security", StringComparison.Ordinal) && path != "/settings/password")
            {
                http.Response.Redirect("/settings/security?change=1");
                return;
            }
            if (demand == "twostep" && !path.StartsWith("/settings/security", StringComparison.Ordinal))
            {
                http.Response.Redirect("/settings/security/two-step?required=1");
                return;
            }
        }
        await next().ConfigureAwait(false);
    }

    private static bool ReadOnlyAllows(string path) =>
        path.StartsWith("/shared/", StringComparison.Ordinal) || path.StartsWith("/settings", StringComparison.Ordinal)
        || path.StartsWith("/auth/", StringComparison.Ordinal) || path == "/sign-out" || path.StartsWith("/contacts", StringComparison.Ordinal)
        || (path.StartsWith("/message/", StringComparison.Ordinal) && (path.EndsWith("/read", StringComparison.Ordinal) || path.EndsWith("/flag", StringComparison.Ordinal)));

    private static bool DemandAllows(string path) =>
        path.StartsWith("/auth/", StringComparison.Ordinal) || path == "/sign-out" || path == "/signed-out"
        || path.StartsWith("/sign-in", StringComparison.Ordinal) || path.StartsWith("/invite/", StringComparison.Ordinal);

    /// <summary>The signed-out page for a sign-out at this moment.</summary>
    /// <param name="idle">True when it was for being idle.</param>
    /// <param name="timeZone">The person's time zone.</param>
    /// <returns>The address.</returns>
    internal static string SignedOutUrl(bool idle, string timeZone) =>
        $"/signed-out?at={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}&tz={Uri.EscapeDataString(timeZone)}" + (idle ? "&idle=1" : string.Empty);

    /// <summary>A message for the next page, sealed so a link cannot put words on it; ten minutes.</summary>
    /// <param name="dp">Data protection.</param>
    /// <param name="text">The message.</param>
    /// <returns>The sealed message.</returns>
    internal static string Note(IDataProtectionProvider dp, string text) =>
        dp.CreateProtector("anjal.notes.v1").ToTimeLimitedDataProtector().Protect(text, TimeSpan.FromMinutes(10));

    /// <summary>Open a sealed message; null when it is not one, or too old.</summary>
    /// <param name="dp">Data protection.</param>
    /// <param name="note">The sealed message.</param>
    /// <returns>The message, or null.</returns>
    internal static string? ReadNote(IDataProtectionProvider dp, string? note)
    {
        if (string.IsNullOrEmpty(note))
        {
            return null;
        }
        try
        {
            return dp.CreateProtector("anjal.notes.v1").ToTimeLimitedDataProtector().Unprotect(note);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The mailbox a reset ticket names, while it lasts (30 minutes).</summary>
    /// <param name="dp">Data protection.</param>
    /// <param name="svc">The mailbox service.</param>
    /// <param name="ticket">The ticket.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The mailbox, or null.</returns>
    internal static async Task<MailboxRow?> MailboxOfTicketAsync(IDataProtectionProvider dp, MailboxService svc, string? ticket, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ticket))
        {
            return null;
        }
        try
        {
            string raw = ResetTicket(dp).Unprotect(ticket);
            // DES-11 S1: a reset for a user name with no account carries a stand-in, never stored.
            if (raw.StartsWith('d') && raw.Split('|') is [{ Length: 33 } id, { } tenant, { } address] && address.Contains('@', StringComparison.Ordinal))
            {
                int at = address.LastIndexOf('@');
                return new MailboxRow
                {
                    Id = Guid.ParseExact(id[1..], "N"),
                    TenantId = Guid.TryParseExact(tenant, "N", out Guid t) ? t : Guid.Empty,
                    LocalPart = address[..at],
                    Domain = address[(at + 1)..],
                    PasswordPbkdf2 = DecoyMark,
                };
            }
            return Guid.TryParseExact(raw, "N", out Guid real) ? (await svc.GetContextAsync(real, ct).ConfigureAwait(false))?.Mailbox : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>True for the stand-in of a reset asked for a user name with no account (DES-11 S1).</summary>
    /// <param name="mailbox">The mailbox from a reset ticket.</param>
    /// <returns>Whether it is the stand-in.</returns>
    internal static bool IsDecoy(MailboxRow mailbox) => string.Equals(mailbox.PasswordPbkdf2, DecoyMark, StringComparison.Ordinal);

    private const string DecoyMark = "decoy:no-account";

    private static ITimeLimitedDataProtector ResetTicket(IDataProtectionProvider dp) => dp.CreateProtector("anjal.reset.v1").ToTimeLimitedDataProtector();

    /// <summary>
    /// DES-11 S4 (owner, 10 Oct 2026, "A"): a change to how the account is protected goes ahead only
    /// when the person gave their password or passkey in the last five minutes (signing in counts);
    /// otherwise they are asked first, and come back to the page they were on.
    /// </summary>
    internal static async ValueTask<object?> RequireRecentProof(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        HttpContext http = context.HttpContext;
        SessionRegistry sessions = http.RequestServices.GetRequiredService<SessionRegistry>();
        if (sessions.ProvedRecently(SessionRegistry.SessionOf(http.User)))
        {
            return await next(context).ConfigureAwait(false);
        }
        string from = Uri.TryCreate(http.Request.Headers.Referer.ToString(), UriKind.Absolute, out Uri? referer) && string.Equals(referer.Host, http.Request.Host.Host, StringComparison.OrdinalIgnoreCase)
            ? referer.PathAndQuery
            : "/settings/security";
        string url = "/settings/security/confirm?next=" + Uri.EscapeDataString(Program.SafeBack(from, "/settings/security"));
        return http.Request.Headers["X-Requested-With"] == "anjal"
            ? Results.Json(new { error = "Confirm it is you first.", next = url })
            : Results.Redirect(url);
    }

    /// <summary>The person and session a sign-in alert's links are about, or null when the link is not good (DES-11 S5).</summary>
    /// <param name="dp">Data protection.</param>
    /// <param name="ticket">The sealed ticket from the link.</param>
    /// <returns>The person and the session.</returns>
    internal static (Guid Person, string Session)? ReadSignInCheck(IDataProtectionProvider dp, string? ticket)
    {
        if (string.IsNullOrEmpty(ticket))
        {
            return null;
        }
        try
        {
            string[] parts = SignInCheckTicket(dp).Unprotect(ticket).Split('|');
            return parts.Length == 2 && Guid.TryParseExact(parts[0], "N", out Guid person) ? (person, parts[1]) : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static void MapSecuritySettings(WebApplication app)
    {
        // DES-11 S4: "Confirm it is you" - the password, or a passkey.
        app.MapPost("/settings/security/confirm", async (HttpContext http, [FromForm] string? password, [FromForm] string? next,
            MailboxService svc, SessionRegistry sessions, LoginThrottle throttle, AuditTrail audit, CancellationToken ct) =>
        {
            Guid? personId = WebmailAuthService.PersonIdOf(http.User);
            (TenantRow Tenant, MailboxRow Mailbox)? context = personId is null ? null : await svc.GetContextAsync(personId.Value, ct).ConfigureAwait(false);
            if (context is null)
            {
                return Results.Redirect("/sign-in");
            }
            string onward = Program.SafeBack(next, "/settings/security");
            string again = "/settings/security/confirm?next=" + Uri.EscapeDataString(onward);
            string client = ClientOf(http);
            string key = "confirm:" + context.Value.Mailbox.Address;
            if (!throttle.IsAllowed(client, key))
            {
                return Results.Redirect(again + "&throttled=1");
            }
            if (!Anjal.Smtp.Pbkdf2Hasher.Verify(password ?? string.Empty, context.Value.Mailbox.PasswordPbkdf2))
            {
                throttle.RecordFailure(client, key);
                await audit.RecordAsync(context.Value.Mailbox.Address, "webmail.confirm.refused", context.Value.Mailbox.Address, client).ConfigureAwait(false);
                return Results.Redirect(again + "&wrong=1");
            }
            throttle.RecordSuccess(client, key);
            sessions.MarkProved(SessionRegistry.SessionOf(http.User));
            return Results.Redirect(onward);
        }).RequireAuthorization();

        app.MapPost("/settings/security/confirm/passkey/options", async (HttpContext http, MailboxService svc, SessionRegistry sessions, CancellationToken ct) =>
        {
            if (WebmailAuthService.PersonIdOf(http.User) is not Guid personId)
            {
                return Results.Json(new { error = "Sign in again." }, statusCode: 400);
            }
            SecurityDocument security = await svc.GetSecurityAsync(personId, ct).ConfigureAwait(false);
            if (security.Passkeys.Count == 0)
            {
                return Results.Json(new { error = "No passkey has been added to this account yet." }, statusCode: 400);
            }
            (string id, byte[] challenge) = sessions.NewChallenge(personId, "confirm");
            return Results.Json(new { challengeId = id, challenge = WebAuthn.ToBase64Url(challenge), rpId = http.Request.Host.Host, allow = security.Passkeys.Select(p => p.Id).ToArray() });
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        app.MapPost("/settings/security/confirm/passkey/verify", async (HttpContext http, [FromForm] string? challengeId, [FromForm] string? id, [FromForm] string? clientDataJSON,
            [FromForm] string? authenticatorData, [FromForm] string? signature, [FromForm] string? next, MailboxService svc, SessionRegistry sessions, CancellationToken ct) =>
        {
            if (WebmailAuthService.PersonIdOf(http.User) is not Guid personId)
            {
                return Results.Json(new { error = "Sign in again." }, statusCode: 400);
            }
            byte[]? challenge = sessions.TakeChallenge(challengeId, personId, "confirm");
            SecurityDocument security = await svc.GetSecurityAsync(personId, ct).ConfigureAwait(false);
            PasskeyRecord? passkey = security.Passkeys.Find(p => p.Id == id);
            if (challenge is null || passkey is null)
            {
                return Results.Json(new { error = challenge is null ? "The request had expired. Try again." : "That passkey is not one of this account's." }, statusCode: 400);
            }
            (uint count, string? error) = WebAuthn.Verify(passkey, clientDataJSON ?? string.Empty, authenticatorData ?? string.Empty, signature ?? string.Empty, challenge, OriginOf(http), http.Request.Host.Host);
            if (error is not null)
            {
                return Results.Json(new { error }, statusCode: 400);
            }
            await svc.PasskeyUsedAsync(personId, passkey.Id, count, ct).ConfigureAwait(false);
            sessions.MarkProved(SessionRegistry.SessionOf(http.User));
            return Results.Json(new { next = Program.SafeBack(next, "/settings/security") });
        }).RequireAuthorization();

        // DES-11 S5: "This wasn't me" in a sign-in alert - that session ends at once, and every trusted device is forgotten.
        app.MapPost("/auth/not-me", async (HttpContext http, [FromForm] string? t, MailboxService svc, SessionRegistry sessions, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (ReadSignInCheck(dp, t) is not (Guid person, string sid))
            {
                return Results.Redirect("/sign-in/check");
            }
            await sessions.EndAsync(person, sid, ct).ConfigureAwait(false);
            await svc.ForgetTrustedDevicesAsync(person, ct).ConfigureAwait(false);
            string who = (await svc.GetContextAsync(person, ct).ConfigureAwait(false))?.Mailbox.Address ?? person.ToString();
            await audit.RecordAsync(who, "webmail.signin.not-me", who, ClientOf(http)).ConfigureAwait(false);
            return Results.Redirect("/sign-in/check?done=1");
        });

        app.MapPost("/settings/password", async (HttpContext http, [FromForm] string current, [FromForm] string next, [FromForm] string? confirm,
            MailboxService svc, SessionRegistry sessions, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string? error = await svc.ChangePasswordAsync(mailboxId.Value, current, next, confirm, ct).ConfigureAwait(false);
            string who = http.User.Identity?.Name ?? string.Empty;
            await audit.RecordAsync(who, error is null ? "webmail.password.changed" : "webmail.password.change-refused", who, ClientOf(http)).ConfigureAwait(false);
            if (error is null)
            {
                await sessions.EndOthersAsync(mailboxId.Value, SessionRegistry.SessionOf(http.User), ct).ConfigureAwait(false);
                sessions.MarkProved(SessionRegistry.SessionOf(http.User));
                // DES-11 S2: told on screen (saved=password) and by mail.
                await svc.SecurityMailAsync(mailboxId.Value, "Your password was changed",
                    "The password of " + who + " was changed. Every other device was signed out, and every trusted device will ask for the second step once more.",
                    ClientOf(http), http.Request.Headers.UserAgent.ToString(), Array.Empty<string>(), ct).ConfigureAwait(false);
            }
            return Results.Redirect(error is null ? "/settings/security?saved=password" : "/settings/security?e=" + Uri.EscapeDataString(Note(dp, error)));
        }).RequireAuthorization();

        app.MapPost("/settings/security/recovery", async (HttpContext http, [FromForm] string? password, [FromForm] string? recovery,
            MailboxService svc, SessionRegistry sessions, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string? error = await svc.SetRecoveryAddressAsync(mailboxId.Value, password ?? string.Empty, recovery ?? string.Empty, ct).ConfigureAwait(false);
            string who = http.User.Identity?.Name ?? string.Empty;
            if (error is null)
            {
                await audit.RecordAsync(who, "webmail.recovery.changed", who, ClientOf(http)).ConfigureAwait(false);
                sessions.MarkProved(SessionRegistry.SessionOf(http.User));
                await svc.SecurityMailAsync(mailboxId.Value, "Your recovery address was changed", "The recovery address of " + who + " was changed. Reset codes now go to the new address.",
                    ClientOf(http), http.Request.Headers.UserAgent.ToString(), Array.Empty<string>(), ct).ConfigureAwait(false);
            }
            return Results.Redirect(error is null ? "/settings/security?saved=recovery" : "/settings/security?e=" + Uri.EscapeDataString(Note(dp, error)));
        }).RequireAuthorization();

        app.MapPost("/settings/security/authenticator/confirm", async (HttpContext http, [FromForm] string? code, MailboxService svc, SessionRegistry sessions, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            (string? error, IReadOnlyList<string> codes) = await svc.ConfirmAuthenticatorAsync(mailboxId.Value, code ?? string.Empty, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Results.Redirect("/settings/security/two-step?e=" + Uri.EscapeDataString(Note(dp, error)));
            }
            string who = http.User.Identity?.Name ?? string.Empty;
            await audit.RecordAsync(who, "webmail.twostep.on", who, ClientOf(http)).ConfigureAwait(false);
            await svc.SecurityMailAsync(mailboxId.Value, "Two-step sign-in is on", "An authenticator app was set up for " + who + ". Signing in now asks for its code as well as the password.", ClientOf(http), http.Request.Headers.UserAgent.ToString(), Array.Empty<string>(), ct).ConfigureAwait(false);
            if (codes.Count > 0)
            {
                Reveal(http, sessions, codes);
                return Results.Redirect("/settings/security/two-step?codes=1&done=1");
            }
            return Results.Redirect("/settings/security?saved=twostep");
        }).RequireAuthorization().AddEndpointFilter(RequireRecentProof);

        app.MapPost("/settings/security/authenticator/remove", async (HttpContext http, [FromForm] string? password, MailboxService svc, SessionRegistry sessions, AuditTrail audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string? error = await svc.RemoveAuthenticatorAsync(mailboxId.Value, password ?? string.Empty, ct).ConfigureAwait(false);
            string who = http.User.Identity?.Name ?? string.Empty;
            if (error is null)
            {
                await audit.RecordAsync(who, "webmail.twostep.authenticator-removed", who, ClientOf(http)).ConfigureAwait(false);
                sessions.MarkProved(SessionRegistry.SessionOf(http.User));
                await svc.SecurityMailAsync(mailboxId.Value, "The authenticator app was turned off", "The authenticator app was removed from " + who + ".",
                    ClientOf(http), http.Request.Headers.UserAgent.ToString(), Array.Empty<string>(), ct).ConfigureAwait(false);
            }
            return Results.Redirect(error is null ? "/settings/security?saved=authenticatoroff" : "/settings/security?e=" + Uri.EscapeDataString(Note(dp, error)));
        }).RequireAuthorization();

        app.MapPost("/settings/security/backup-codes", async (HttpContext http, MailboxService svc, SessionRegistry sessions, AuditTrail audit, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            IReadOnlyList<string> codes = await svc.NewBackupCodesAsync(mailboxId.Value, ct).ConfigureAwait(false);
            string who = http.User.Identity?.Name ?? string.Empty;
            await audit.RecordAsync(who, "webmail.twostep.backup-codes-made", who, ClientOf(http)).ConfigureAwait(false);
            await svc.SecurityMailAsync(mailboxId.Value, "New backup codes were made", "New backup codes were made for " + who + ". The earlier ones no longer work.", ClientOf(http), http.Request.Headers.UserAgent.ToString(), Array.Empty<string>(), ct).ConfigureAwait(false);
            Reveal(http, sessions, codes);
            return Results.Redirect("/settings/security/two-step?codes=1");
        }).RequireAuthorization().AddEndpointFilter(RequireRecentProof).AddEndpointFilter(Program.RequireAntiforgery);

        app.MapPost("/settings/security/passkey/options", async (HttpContext http, MailboxService svc, SessionRegistry sessions, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            (TenantRow Tenant, MailboxRow Mailbox)? context = mailboxId is null ? null : await svc.GetContextAsync(mailboxId.Value, ct).ConfigureAwait(false);
            if (context is null)
            {
                return Results.Json(new { error = "Sign in again." }, statusCode: 400);
            }
            SecurityDocument security = await svc.GetSecurityAsync(mailboxId!.Value, ct).ConfigureAwait(false);
            (string id, byte[] challenge) = sessions.NewChallenge(mailboxId.Value, "register");
            string org = context.Value.Tenant.DisplayName.Trim().Length > 0 ? context.Value.Tenant.DisplayName.Trim() : context.Value.Tenant.Slug;
            return Results.Json(new
            {
                challengeId = id,
                challenge = WebAuthn.ToBase64Url(challenge),
                rp = new { id = http.Request.Host.Host, name = org + " mail" },
                user = new
                {
                    id = WebAuthn.ToBase64Url(mailboxId.Value.ToByteArray()),
                    name = context.Value.Mailbox.Address,
                    displayName = context.Value.Mailbox.DisplayName.Trim().Length > 0 ? context.Value.Mailbox.DisplayName.Trim() : context.Value.Mailbox.Address,
                },
                exclude = security.Passkeys.Select(p => p.Id).ToArray(),
            });
        }).RequireAuthorization().AddEndpointFilter(RequireRecentProof).AddEndpointFilter(Program.RequireAntiforgery);

        app.MapPost("/settings/security/passkey/add", async (HttpContext http, [FromForm] string? challengeId, [FromForm] string? clientDataJSON, [FromForm] string? attestationObject,
            MailboxService svc, SessionRegistry sessions, AuditTrail audit, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Json(new { error = "Sign in again." }, statusCode: 400);
            }
            byte[]? challenge = sessions.TakeChallenge(challengeId, mailboxId.Value, "register");
            if (challenge is null)
            {
                return Results.Json(new { error = "The request had expired. Try again." }, statusCode: 400);
            }
            (PasskeyRecord? passkey, string? error) = WebAuthn.Register(clientDataJSON ?? string.Empty, attestationObject ?? string.Empty, challenge, OriginOf(http), http.Request.Host.Host,
                DeviceName.Of(http.Request.Headers.UserAgent.ToString()), DateTimeOffset.UtcNow);
            error ??= await svc.AddPasskeyAsync(mailboxId.Value, passkey!, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Results.Json(new { error }, statusCode: 400);
            }
            string who = http.User.Identity?.Name ?? string.Empty;
            await audit.RecordAsync(who, "webmail.twostep.passkey-added", who, ClientOf(http)).ConfigureAwait(false);
            await svc.SecurityMailAsync(mailboxId.Value, "A passkey was added", "A passkey was added to " + who + ".", ClientOf(http), http.Request.Headers.UserAgent.ToString(), Array.Empty<string>(), ct).ConfigureAwait(false);
            return Results.Json(new { next = "/settings/security?saved=passkey" });
        }).RequireAuthorization().AddEndpointFilter(RequireRecentProof);

        app.MapPost("/settings/security/passkey/remove", async (HttpContext http, [FromForm] string? id, MailboxService svc, AuditTrail audit, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            bool removed = await svc.RemovePasskeyAsync(mailboxId.Value, id ?? string.Empty, ct).ConfigureAwait(false);
            string who = http.User.Identity?.Name ?? string.Empty;
            if (removed)
            {
                await audit.RecordAsync(who, "webmail.twostep.passkey-removed", who, ClientOf(http)).ConfigureAwait(false);
                await svc.SecurityMailAsync(mailboxId.Value, "A passkey was removed", "A passkey was removed from " + who + ".", ClientOf(http), http.Request.Headers.UserAgent.ToString(), Array.Empty<string>(), ct).ConfigureAwait(false);
            }
            return Results.Redirect("/settings/security?saved=passkeyremoved");
        }).RequireAuthorization().AddEndpointFilter(RequireRecentProof);

        app.MapPost("/settings/security/sessions/end", async (HttpContext http, [FromForm] string? sid, SessionRegistry sessions, AuditTrail audit, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string? current = SessionRegistry.SessionOf(http.User);
            IReadOnlyList<SessionRecord> mine = await sessions.ListAsync(mailboxId.Value, current, ct).ConfigureAwait(false);
            if (sid is null || !mine.Any(s => s.Id == sid))
            {
                return Results.Redirect("/settings/security");
            }
            // DES-11 S4: ending another device asks to confirm it is you; signing this one out does not.
            if (sid != current && !sessions.ProvedRecently(current))
            {
                return Results.Redirect("/settings/security/confirm?next=" + Uri.EscapeDataString("/settings/security"));
            }
            await sessions.EndAsync(mailboxId.Value, sid, ct).ConfigureAwait(false);
            string who = http.User.Identity?.Name ?? string.Empty;
            await audit.RecordAsync(who, "webmail.session.ended", who, ClientOf(http)).ConfigureAwait(false);
            if (sid == current)
            {
                await http.SignOutAsync(Scheme).ConfigureAwait(false);
                http.Response.Headers["Clear-Site-Data"] = "\"cache\", \"storage\"";
                return Results.Redirect(SignedOutUrl(false, WebmailAuthService.ClockOf(http.User).ZoneId));
            }
            return Results.Redirect("/settings/security?saved=device");
        }).RequireAuthorization();

        app.MapPost("/settings/security/sessions/others", async (HttpContext http, MailboxService svc, SessionRegistry sessions, AuditTrail audit, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            int ended = await sessions.EndOthersAsync(mailboxId.Value, SessionRegistry.SessionOf(http.User), ct).ConfigureAwait(false);
            await svc.ForgetTrustedDevicesAsync(mailboxId.Value, ct).ConfigureAwait(false);
            string who = http.User.Identity?.Name ?? string.Empty;
            await audit.RecordAsync(who, "webmail.session.ended-others", who + " (" + ended + ")", ClientOf(http)).ConfigureAwait(false);
            await svc.SecurityMailAsync(mailboxId.Value, "Your other devices were signed out", "Every other device signed in to " + who + " was signed out, and every trusted device was forgotten.", ClientOf(http), http.Request.Headers.UserAgent.ToString(), Array.Empty<string>(), ct).ConfigureAwait(false);
            return Results.Redirect("/settings/security?saved=others");
        }).RequireAuthorization().AddEndpointFilter(RequireRecentProof).AddEndpointFilter(Program.RequireAntiforgery);
    }

    /// <summary>
    /// Finish a sign-in: start the session, add its claims, and set the cookie -
    /// for the session only on a shared computer (and at most 12 hours), for
    /// 30 days on the person's own. Returns where to go, or null when the
    /// mailbox can no longer sign in.
    /// </summary>
    private static async Task<string?> CompleteAsync(HttpContext http, Guid mailboxId, bool shared, string via, CancellationToken ct)
    {
        WebmailAuthService auth = http.RequestServices.GetRequiredService<WebmailAuthService>();
        SessionRegistry sessions = http.RequestServices.GetRequiredService<SessionRegistry>();
        ClaimsPrincipal? principal = await auth.PrincipalForAsync(mailboxId, ct).ConfigureAwait(false);
        if (principal is null)
        {
            return null;
        }
        string sid = await sessions.StartAsync(mailboxId, shared, http.Request.Headers.UserAgent.ToString(), ClientOf(http), ct).ConfigureAwait(false);
        principal = WebmailAuthService.WithSession(principal, sid, shared);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await http.SignInAsync(Scheme, principal, new AuthenticationProperties
        {
            IsPersistent = !shared,
            IssuedUtc = now,
            ExpiresUtc = now + (shared ? SessionRegistry.SharedLife : SessionRegistry.OwnLife),
        }).ConfigureAwait(false);
        string who = principal.Identity?.Name ?? string.Empty;
        AuditTrail audit = http.RequestServices.GetRequiredService<AuditTrail>();
        await audit.RecordAsync(who, "webmail.signin", who + " (" + via + (shared ? ", shared computer" : string.Empty) + ")", ClientOf(http)).ConfigureAwait(false);
        // rc.15 (item 59): a sign-in from a network this person has not used before is noted, with its town and country.
        // DES-11 S5 (owner, 10 Oct 2026, "A"): and the person is told, always, from a new network or a new device.
        sessions.MarkProved(sid);
        try
        {
            MailboxService svc = http.RequestServices.GetRequiredService<MailboxService>();
            (bool isNew, string place) = await svc.NoteSignInNetworkAsync(mailboxId, ClientOf(http), now, ct).ConfigureAwait(false);
            if (isNew)
            {
                await audit.RecordAsync(who, "webmail.signin.new-network", who, ClientOf(http), place).ConfigureAwait(false);
            }
            if (await svc.NoteSignInAsync(mailboxId, ClientOf(http), http.Request.Headers.UserAgent.ToString(), isNew, now, ct).ConfigureAwait(false))
            {
                IDataProtectionProvider dp = http.RequestServices.GetRequiredService<IDataProtectionProvider>();
                string ticket = SignInCheckTicket(dp).Protect(mailboxId.ToString("N") + "|" + sid, TimeSpan.FromDays(7));
                string origin = OriginOf(http);
                await svc.SecurityMailAsync(mailboxId, "New sign-in to " + who,
                    "Someone signed in to " + who + " from a network or a device not used before.",
                    ClientOf(http), http.Request.Headers.UserAgent.ToString(), new[]
                    {
                        "This was me: " + origin + "/sign-in/check?a=me&t=" + Uri.EscapeDataString(ticket),
                        "This wasn't me (signs that device out at once): " + origin + "/sign-in/check?a=not-me&t=" + Uri.EscapeDataString(ticket),
                    }, ct).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // Noting the network never stands in the way of signing in.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[sign-in] network not noted: {ex.GetType().Name}");
        }
#pragma warning restore CA1031
        // rc.15 (owner, 7 Oct): everyone lands on their dashboard after signing in.
        return "/dashboard";
    }

    // DES-11 S3 (owner, 10 Oct 2026, "A"): five wrong second-step codes lock the second step for
    // fifteen minutes on every device; the person is mailed, and the organisation's log has it.
    private static async Task LockAsync(HttpContext http, MailboxService svc, SessionRegistry sessions, AuditTrail audit, PendingSignIn pending, CancellationToken ct)
    {
        sessions.RemovePending(pending.Id);
        http.Response.Cookies.Delete(PendingCookie);
        await svc.LockSecondStepAsync(pending.MailboxId, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        await audit.RecordAsync(pending.Address, "webmail.signin.second-step-locked", pending.Address, ClientOf(http)).ConfigureAwait(false);
        await svc.SecurityMailAsync(pending.MailboxId, "Your sign-in was locked for fifteen minutes",
            "Someone typed the password of " + pending.Address + " correctly but failed the second step five times. The second step is locked for fifteen minutes on every device. Your password still works for nothing without the second step.",
            ClientOf(http), http.Request.Headers.UserAgent.ToString(), Array.Empty<string>(), ct).ConfigureAwait(false);
    }

    /// <summary>The seal on the two links in a sign-in alert (DES-11 S5): the person and the session, for 7 days.</summary>
    /// <param name="dp">Data protection.</param>
    /// <returns>The protector.</returns>
    internal static ITimeLimitedDataProtector SignInCheckTicket(IDataProtectionProvider dp) => dp.CreateProtector("anjal.signin-check.v1").ToTimeLimitedDataProtector();

    private static async Task<string> FinishSecondStepAsync(HttpContext http, PendingSignIn pending, bool trust, string via, CancellationToken ct)
    {
        SessionRegistry sessions = http.RequestServices.GetRequiredService<SessionRegistry>();
        sessions.RemovePending(pending.Id);
        http.Response.Cookies.Delete(PendingCookie);
        if (trust && !pending.Shared)
        {
            MailboxService svc = http.RequestServices.GetRequiredService<MailboxService>();
            string token = await svc.TrustDeviceAsync(pending.MailboxId, DeviceName.Of(http.Request.Headers.UserAgent.ToString()), ct).ConfigureAwait(false);
            http.Response.Cookies.Append(TrustCookie, token, ShortCookie(http, MailboxService.TrustLife));
        }
        return await CompleteAsync(http, pending.MailboxId, pending.Shared, via, ct).ConfigureAwait(false) ?? "/sign-in?error=1";
    }

    private static void Reveal(HttpContext http, SessionRegistry sessions, IReadOnlyList<string> codes) =>
        sessions.Reveal(SessionRegistry.SessionOf(http.User) ?? string.Empty, codes);

    private static CookieOptions ShortCookie(HttpContext http, TimeSpan life) => new()
    {
        HttpOnly = true,
        Secure = http.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = life,
        IsEssential = true,
    };

    private static string ClientOf(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static string OriginOf(HttpContext http) => http.Request.Scheme + "://" + http.Request.Host.Value;
}
