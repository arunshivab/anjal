using System.Globalization;
using Anjal.Viewing;
using Anjal.Webmail.Services;
using Microsoft.AspNetCore.Mvc;
using ViewResult = Anjal.Viewing.ViewResult;

namespace Anjal.Webmail;

/// <summary>
/// rc.29 (DES-22): the small answers the viewer's page asks for while a file is open - a page's picture and text,
/// Find, a Word document's pictures, a picture inside an archive - and the forms it posts (a file's password).
/// Every answer comes from the separate viewer process; the webmail only passes the file's bytes to it.
/// </summary>
internal static class ViewerEndpoints
{
    private const string Base = "/message/{id:guid}/attachment/{index:int}/view";

    internal static void Map(WebApplication app)
    {
        // A PDF page as a picture, at a width in pixels (60-2,000).
        app.MapGet(Base + "/page/{page:int}", async (HttpContext http, Guid id, int index, int page, int? w, string? entry, MailboxService svc, Words words, CancellationToken ct) =>
        {
            ViewResult r = await AskAsync(http, svc, words, id, index, new ViewRequest { Kind = ViewKinds.PdfPage, Page = page, Width = Math.Clamp(w ?? 1200, 60, 2000), Entry = entry }, ct).ConfigureAwait(false);
            return r.Ok && r.Files.Count > 0 && FileKinds.PictureType(r.Files[0]) == "image/png" ? Picture(http, r.Files[0], "image/png") : Results.NotFound();
        }).RequireAuthorization();

        // A PDF page's text and where each run sits, for the text laid over the picture (select, copy, find).
        app.MapGet(Base + "/text/{page:int}", async (HttpContext http, Guid id, int index, int page, string? entry, MailboxService svc, Words words, CancellationToken ct) =>
        {
            ViewResult r = await AskAsync(http, svc, words, id, index, new ViewRequest { Kind = ViewKinds.PdfText, Page = page, Entry = entry }, ct).ConfigureAwait(false);
            return r.Ok && r.Answer.Json is string json ? Results.Text(json, "application/json") : Results.NotFound();
        }).RequireAuthorization();

        // Find in a PDF.
        app.MapGet(Base + "/find", async (HttpContext http, Guid id, int index, string? q, string? entry, MailboxService svc, Words words, CancellationToken ct) =>
        {
            string query = (q ?? string.Empty).Trim();
            if (query.Length is 0 or > 200)
            {
                return Results.Text("{\"Hits\":[]}", "application/json");
            }
            ViewResult r = await AskAsync(http, svc, words, id, index, new ViewRequest { Kind = ViewKinds.PdfFind, Query = query, Entry = entry }, ct).ConfigureAwait(false);
            return r.Ok && r.Answer.Json is string json ? Results.Text(json, "application/json") : Results.Text("{\"Hits\":[]}", "application/json");
        }).RequireAuthorization();

        // D-367: a PDF without its scripts, actions and embedded files, as a download.
        app.MapGet(Base + "/clean", async (HttpContext http, Guid id, int index, string? entry, MailboxService svc, Words words, CancellationToken ct) =>
        {
            ViewResult r = await AskAsync(http, svc, words, id, index, new ViewRequest { Kind = ViewKinds.PdfClean, Entry = entry }, ct).ConfigureAwait(false);
            return r.Ok && r.Files.Count > 0 ? Results.File(r.Files[0], "application/octet-stream", "without-scripts.pdf") : Results.NotFound();
        }).RequireAuthorization();

        // A Word document's picture (re-drawn as PNG by the viewer), by its place among the document's pictures.
        app.MapGet(Base + "/word-picture/{k:int}", async (HttpContext http, Guid id, int index, int k, string? entry, MailboxService svc, Words words, CancellationToken ct) =>
        {
            ViewResult r = await AskAsync(http, svc, words, id, index, new ViewRequest { Kind = ViewKinds.Word, Entry = entry }, ct).ConfigureAwait(false);
            return r.Ok && k >= 0 && k < r.Files.Count && FileKinds.PictureType(r.Files[k]) == "image/png" ? Picture(http, r.Files[k], "image/png") : Results.NotFound();
        }).RequireAuthorization();

        // A picture inside an archive (AC-17), its type judged by its bytes.
        app.MapGet(Base + "/picture", async (HttpContext http, Guid id, int index, string? entry, MailboxService svc, Words words, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(entry))
            {
                return Results.NotFound();
            }
            ViewResult r = await AskAsync(http, svc, words, id, index, new ViewRequest { Kind = ViewKinds.Picture, Entry = entry }, ct).ConfigureAwait(false);
            return r.Ok && r.Files.Count > 0 && FileKinds.PictureType(r.Files[0]) is string type ? Picture(http, r.Files[0], type) : Results.NotFound();
        }).RequireAuthorization();

        // DES-22 §3.1: a file's password, for this view only - kept in the session's memory for 10 minutes, never on
        // disk, never logged.
        app.MapPost(Base + "/password", (HttpContext http, Guid id, int index, [FromForm] string? password, [FromForm] string? entry, [FromForm] string? back, MailboxService svc) =>
        {
            if (WebmailAuthService.MailboxIdOf(http.User) is not Guid mailboxId)
            {
                return Results.Redirect("/sign-in");
            }
            string target = SafeBack(back, id);
            if (svc.Viewer is ViewerAccess viewer && !string.IsNullOrEmpty(password) && password.Length <= 256)
            {
                viewer.RememberPassword(SessionKey(http, mailboxId), MailboxService.FileKey(mailboxId, id, index, string.IsNullOrEmpty(entry) ? null : entry), password);
            }
            return Results.Redirect(target);
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        // AC-17: one file from inside an archive (or an archive inside it, "3/5"), downloaded - never a program,
        // never from a locked or refused archive; served like every attachment, as a download of no particular type.
        app.MapGet("/message/{id:guid}/attachment/{index:int}/zip", async (HttpContext http, Guid id, int index, string? entry, MailboxService svc, Words words, CancellationToken ct) =>
            await EntryDownloadAsync(http, svc, words, id, index, entry, ct).ConfigureAwait(false)).RequireAuthorization();
        app.MapGet("/message/{id:guid}/attachment/{index:int}/zip/{entry:int}", async (HttpContext http, Guid id, int index, int entry, MailboxService svc, Words words, CancellationToken ct) =>
            await EntryDownloadAsync(http, svc, words, id, index, entry.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false)).RequireAuthorization();
    }

    /// <summary>The key a session's kept answers live under: its session, or the mailbox when it has none.</summary>
    /// <param name="http">The request.</param>
    /// <param name="mailboxId">The mailbox.</param>
    /// <returns>The key.</returns>
    internal static string SessionKey(HttpContext http, Guid mailboxId) =>
        SessionRegistry.SessionOf(http.User) is string sid ? sid + "/" + mailboxId.ToString("N") : "m/" + mailboxId.ToString("N");

    private static async Task<IResult> EntryDownloadAsync(HttpContext http, MailboxService svc, Words words, Guid id, int index, string? entry, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(entry))
        {
            return Results.NotFound();
        }
        ViewResult r = await AskAsync(http, svc, words, id, index, new ViewRequest { Kind = ViewKinds.ArchiveEntry, Entry = entry }, ct).ConfigureAwait(false);
        FileOut? file = r.Ok ? ViewJson.ReadFile(r.Answer.Json) : null;
        return file is null || r.Files.Count == 0 ? Results.NotFound() : Results.File(r.Files[0], "application/octet-stream", file.Name);
    }

    private static async Task<ViewResult> AskAsync(HttpContext http, MailboxService svc, Words words, Guid id, int index, ViewRequest request, CancellationToken ct)
    {
        if (WebmailAuthService.MailboxIdOf(http.User) is not Guid mailboxId)
        {
            return ViewResult.Of(ViewStatus.Unavailable);
        }
        return await svc.ViewAsync(mailboxId, id, index, request, SessionKey(http, mailboxId), WebmailAuthService.LanguageOf(http.User, words), ct).ConfigureAwait(false);
    }

    // A picture made or checked by the viewer, served so it can never run as anything else in the webmail's page.
    private static IResult Picture(HttpContext http, byte[] bytes, string type)
    {
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.Bytes(bytes, type);
    }

    // The page to go back to after a form: only a page of this webmail.
    private static string SafeBack(string? back, Guid id) =>
        !string.IsNullOrEmpty(back) && back.StartsWith('/') && !back.StartsWith("//", StringComparison.Ordinal) && !back.Contains('\\', StringComparison.Ordinal)
            ? back
            : "/message/" + id.ToString("D");
}
