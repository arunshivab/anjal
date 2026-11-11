using Anjal.Webmail.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace Anjal.Webmail;

/// <summary>rc.28 (DES-20): Settings › Mail apps - app passwords, and bringing old mail in.</summary>
internal static class MailAppEndpoints
{
    internal static void Map(WebApplication app)
    {
        app.MapPost("/settings/apps/new", async (HttpContext http, [FromForm] string? name, MailboxService svc, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (WebmailAuthService.PersonIdOf(http.User) is not Guid person)
            {
                return Results.Redirect("/sign-in");
            }
            NewAppPassword made = await svc.MakeAppPasswordAsync(person, name, http.User.Identity?.Name ?? string.Empty, ct).ConfigureAwait(false);
            // The password is shown once, on the next page, sealed in the address for ten minutes.
            return Results.Redirect(made.Error is null
                ? "/settings/apps?made=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, (name ?? string.Empty).Trim() + "\n" + made.Password))
                : "/settings/apps?err=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, made.Error)));
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        app.MapPost("/settings/apps/{id:guid}/remove", async (HttpContext http, Guid id, MailboxService svc, CancellationToken ct) =>
        {
            if (WebmailAuthService.PersonIdOf(http.User) is not Guid person)
            {
                return Results.Redirect("/sign-in");
            }
            bool removed = await svc.RemoveAppPasswordAsync(person, id, http.User.Identity?.Name ?? string.Empty, ct).ConfigureAwait(false);
            return Results.Redirect(removed ? "/settings/apps?removed=1" : "/settings/apps");
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        // An mbox, a zip of .eml files or one .eml, up to 2 GB (D-346): kept on disk for the import worker.
        app.MapPost("/settings/import/file", async (HttpContext http, [FromForm] string? folder, IFormFile? file, MailboxService svc, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (WebmailAuthService.PersonIdOf(http.User) is not Guid person)
            {
                return Results.Redirect("/sign-in");
            }
            if (file is null || file.Length == 0)
            {
                return Results.Redirect("/settings/apps?err=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, "Choose a file to bring in.")));
            }
            await using Stream upload = file.OpenReadStream();
            string? error = await svc.StartFileImportAsync(person, upload, file.FileName, folder, ct).ConfigureAwait(false);
            return Results.Redirect(error is null ? "/settings/apps?importing=1#import" : "/settings/apps?err=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error)));
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery)
          .WithMetadata(new RequestSizeLimitAttribute(MailboxService.MaxImportBytes + (1024 * 1024)))
          .WithFormOptions(multipartBodyLengthLimit: MailboxService.MaxImportBytes + (1024 * 1024));

        app.MapPost("/settings/import/server", async (HttpContext http, [FromForm] string? host, [FromForm] string? user, [FromForm] string? password, MailboxService svc, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (WebmailAuthService.PersonIdOf(http.User) is not Guid person)
            {
                return Results.Redirect("/sign-in");
            }
            (Guid? job, string? error) = await svc.LookAtServerAsync(person, host, user, password, ct).ConfigureAwait(false);
            return Results.Redirect(job is Guid j ? "/settings/apps?choose=" + j.ToString("N") + "#import" : "/settings/apps?err=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error ?? "Not started.")) + "#import");
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        app.MapPost("/settings/import/{id:guid}/folders", async (HttpContext http, Guid id, MailboxService svc, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (WebmailAuthService.PersonIdOf(http.User) is not Guid person)
            {
                return Results.Redirect("/sign-in");
            }
            string[] chosen = http.Request.Form["folder"].Where(f => f is not null).Select(f => f!).ToArray();
            string? error = await svc.ChooseImportFoldersAsync(person, id, chosen, ct).ConfigureAwait(false);
            return Results.Redirect(error is null ? "/settings/apps?importing=1#import" : "/settings/apps?choose=" + id.ToString("N") + "&err=" + Uri.EscapeDataString(AuthEndpoints.Note(dp, error)) + "#import");
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        app.MapPost("/settings/import/{id:guid}/stop", async (HttpContext http, Guid id, MailboxService svc, CancellationToken ct) =>
        {
            if (WebmailAuthService.PersonIdOf(http.User) is not Guid person)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.StopImportAsync(person, id, ct).ConfigureAwait(false);
            return Results.Redirect("/settings/apps#import");
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);
    }
}

/// <summary>rc.28 (DES-20 §5): runs waiting imports in the background, one at a time.</summary>
internal sealed class ImportWorker(MailboxService mail) : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int ran = await mail.RunImportsAsync(stoppingToken).ConfigureAwait(false);
                if (ran > 0)
                {
                    Console.WriteLine($"[import] {ran} import job(s) ran.");
                }
            }
#pragma warning disable CA1031 // An import that fails is marked so; the worker goes on with the next.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[import] {ex.GetType().Name}: {ex.Message}");
            }
#pragma warning restore CA1031
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
