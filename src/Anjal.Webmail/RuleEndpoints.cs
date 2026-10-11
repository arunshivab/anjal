using Anjal.Mailbox;
using Anjal.Webmail.Services;
using Microsoft.AspNetCore.Mvc;

namespace Anjal.Webmail;

/// <summary>rc.12 (items 24, 31): the endpoints behind Settings, Folders and rules.</summary>
internal static class RuleEndpoints
{
    internal static void Map(WebApplication app)
    {
        app.MapPost("/settings/rules/save", async (HttpContext http, [FromForm] string? id, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            IFormCollection form = await http.Request.ReadFormAsync(ct).ConfigureAwait(false);
            string[] fields = form["field"].Select(v => v ?? string.Empty).ToArray();
            string[] ops = form["op"].Select(v => v ?? string.Empty).ToArray();
            string[] values = form["value"].Select(v => v ?? string.Empty).ToArray();
            var conditions = new List<RuleCondition>();
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].Length == 0)
                {
                    continue;
                }
                conditions.Add(new RuleCondition
                {
                    Field = fields[i],
                    Op = i < ops.Length ? ops[i] : "contains",
                    Value = i < values.Length ? values[i] : string.Empty,
                });
            }
            string moveTo = form["moveTo"].ToString();
            string newFolder = form["newFolder"].ToString().Trim();
            if (newFolder.Length > 0)
            {
                string? folderError = await svc.CreateFolderAsync(mailboxId.Value, newFolder, ct).ConfigureAwait(false);
                if (folderError is not null && !folderError.Contains("already", StringComparison.Ordinal))
                {
                    return Results.Redirect("/settings/rules?rule=new&problem=" + Uri.EscapeDataString(folderError));
                }
                moveTo = newFolder;
            }
            var rule = new MailRule
            {
                // The id is bound as a form field, which also turns on this endpoint's antiforgery check.
                Id = Guid.TryParse(id, out Guid existing) ? existing : Guid.NewGuid(),
                Name = form["name"].ToString(),
                Enabled = form["enabled"] == "1",
                Conditions = conditions,
                MoveTo = moveTo,
                MarkRead = form["markRead"] == "1",
                Flag = form["flag"] == "1",
            };
            string? error = await svc.SaveRuleAsync(mailboxId.Value, rule, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Results.Redirect($"/settings/rules?rule={(existing == Guid.Empty ? "new" : existing.ToString())}&problem={Uri.EscapeDataString(error)}");
            }
            if (form["applyExisting"] == "1")
            {
                await svc.TryRuleAsync(mailboxId.Value, rule, apply: true, ct).ConfigureAwait(false);
            }
            return Results.Redirect($"/settings/rules?rule={rule.Id}&notice=saved" + (form["trial"] == "1" ? "&trial=1" : string.Empty));
        }).RequireAuthorization();

        app.MapPost("/settings/rules/{id:guid}/delete", async (HttpContext http, Guid id, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.DeleteRuleAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            return Results.Redirect("/settings/rules?notice=deleted");
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        app.MapPost("/settings/rules/{id:guid}/move", async (HttpContext http, Guid id, [FromForm] string? up, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.MoveRuleAsync(mailboxId.Value, id, up == "1", ct).ConfigureAwait(false);
            return Results.Redirect("/settings/rules");
        }).RequireAuthorization();

        app.MapPost("/settings/rules/{id:guid}/toggle", async (HttpContext http, Guid id, [FromForm] string? on, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.SetRuleEnabledAsync(mailboxId.Value, id, on == "1", ct).ConfigureAwait(false);
            return Results.Redirect("/settings/rules");
        }).RequireAuthorization();

        // Item 31: the ready-made DMARC reports rule, offered when a report arrives.
        app.MapPost("/settings/rules/dmarc", async (HttpContext http, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            MailRule rule = MailboxService.DmarcRule();
            if (!(await svc.ListRulesAsync(mailboxId.Value, ct).ConfigureAwait(false)).Any(r => r.MoveTo == rule.MoveTo))
            {
                await svc.SaveRuleAsync(mailboxId.Value, rule, ct).ConfigureAwait(false);
                await svc.TryRuleAsync(mailboxId.Value, rule, apply: true, ct).ConfigureAwait(false);
            }
            return Results.Redirect("/folder/" + Uri.EscapeDataString(rule.MoveTo));
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        app.MapPost("/settings/folders/new", async (HttpContext http, [FromForm] string? name, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string? error = await svc.CreateFolderAsync(mailboxId.Value, name ?? string.Empty, ct).ConfigureAwait(false);
            return Results.Redirect(error is null ? "/settings/rules?notice=folder" : "/settings/rules?problem=" + Uri.EscapeDataString(error));
        }).RequireAuthorization();

        app.MapPost("/settings/folders/delete", async (HttpContext http, [FromForm] string? name, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            string? error = await svc.DeleteFolderAsync(mailboxId.Value, name ?? string.Empty, ct).ConfigureAwait(false);
            return Results.Redirect(error is null ? "/settings/rules?notice=folderremoved" : "/settings/rules?problem=" + Uri.EscapeDataString(error));
        }).RequireAuthorization();
    }
}
