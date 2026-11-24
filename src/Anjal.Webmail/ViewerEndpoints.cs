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

        // rc.30 (DES-22 §3.4): a DICOM file's picture (0) and, for several frames, its strip (1) - drawn by the viewer,
        // given as PNG; only with the DICOM switch on (D-372).
        app.MapGet(Base + "/dicom/{part:int}", async (HttpContext http, Guid id, int index, int part, string? entry, MailboxService svc, Words words, CancellationToken ct) =>
        {
            if (svc.Viewer is not ViewerAccess { DicomOn: true } || part is < 0 or > 1)
            {
                return Results.NotFound();
            }
            ViewResult r = await AskAsync(http, svc, words, id, index, new ViewRequest { Kind = ViewKinds.Dicom, Entry = string.IsNullOrEmpty(entry) ? null : entry }, ct).ConfigureAwait(false);
            return r.Ok && r.Files.Count > part && FileKinds.PictureType(r.Files[part]) == "image/png" ? Picture(http, r.Files[part], "image/png") : Results.NotFound();
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
    // rc.29 part 2 (DES-22 §7): the PDF editor. The pages as they will be (arranged and joined) are made by the viewer
    // and kept in this session's memory under a key; Save makes the new file and puts it on a new draft. Every post
    // carries the antiforgery token; the answers are JSON for the editor's script.
    internal static void MapEditor(WebApplication app)
    {
        const string edit = "/message/{id:guid}/attachment/{index:int}/edit";

        app.MapPost(edit + "/arrange", async (HttpContext http, Guid id, int index, [FromForm] string? plan, [FromForm] string? join, [FromForm] string? entry, MailboxService svc, Words words, CancellationToken ct) =>
        {
            if (WebmailAuthService.MailboxIdOf(http.User) is not Guid mailboxId)
            {
                return Results.Unauthorized();
            }
            Lexicon l = words.For(WebmailAuthService.LanguageOf(http.User, words));
            string session = SessionKey(http, mailboxId);
            string? place = string.IsNullOrEmpty(entry) ? null : entry;
            if (MailboxService.ReadEditPlan(plan) is not EditPlan p || MailboxService.ReadJoin(join) is not List<int> joined
                || await svc.EditTargetAsync(mailboxId, id, index, place, session, ct).ConfigureAwait(false) is not { Editable: true })
            {
                return Results.Json(new { problem = l["Something in the changes did not fit this file. Nothing was saved."] }, statusCode: 400);
            }
            (string? key, PdfInfo? info, List<PagePick>? picks, string status) = await svc.ArrangeEditAsync(mailboxId, id, index, place, p.Pages, joined, session, ct).ConfigureAwait(false);
            return key is null || info is null || picks is null || picks.Count != info.Pages.Count
                ? Results.Json(new { problem = l[EditWords(status, status == EditRefusals.Layers ? new EditReport { Refused = status } : null)] }, statusCode: 409)
                : Results.Json(new { key, pages = info.Pages.Select((s, i) => new { w = s.W, h = s.H, file = picks[i].File, page = picks[i].Page, turn = picks[i].Turn }) });
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        app.MapGet(edit + "/{key}/page/{page:int}", async (HttpContext http, Guid id, int index, string key, int page, int? w, MailboxService svc, CancellationToken ct) =>
        {
            ViewResult r = await ArrangedAsync(http, svc, key, new ViewRequest { Kind = ViewKinds.PdfPage, Page = page, Width = Math.Clamp(w ?? 1200, 60, 2000) }, ct).ConfigureAwait(false);
            return r.Ok && r.Files.Count > 0 && FileKinds.PictureType(r.Files[0]) == "image/png" ? Picture(http, r.Files[0], "image/png") : Results.NotFound();
        }).RequireAuthorization();

        app.MapGet(edit + "/{key}/text/{page:int}", async (HttpContext http, Guid id, int index, string key, int page, MailboxService svc, CancellationToken ct) =>
        {
            ViewResult r = await ArrangedAsync(http, svc, key, new ViewRequest { Kind = ViewKinds.PdfText, Page = page }, ct).ConfigureAwait(false);
            return r.Ok && r.Answer.Json is string json ? Results.Text(json, "application/json") : Results.NotFound();
        }).RequireAuthorization();

        app.MapGet(edit + "/{key}/find", async (HttpContext http, Guid id, int index, string key, string? q, MailboxService svc, CancellationToken ct) =>
        {
            string query = (q ?? string.Empty).Trim();
            if (query.Length is 0 or > 200)
            {
                return Results.Text("{\"Hits\":[]}", "application/json");
            }
            ViewResult r = await ArrangedAsync(http, svc, key, new ViewRequest { Kind = ViewKinds.PdfFind, Query = query }, ct).ConfigureAwait(false);
            return r.Ok && r.Answer.Json is string json ? Results.Text(json, "application/json") : Results.Text("{\"Hits\":[]}", "application/json");
        }).RequireAuthorization();

        // DES-22 §7.6: the patient signs in the file (or in its pages as they will be), offered as boxes to keep or drop.
        app.MapGet(edit + "/signs", async (HttpContext http, Guid id, int index, string? entry, MailboxService svc, Words words, CancellationToken ct) =>
        {
            // Only for a file the person may edit (never one from a locked archive, AC-20).
            if (WebmailAuthService.MailboxIdOf(http.User) is not Guid mailboxId
                || await svc.EditTargetAsync(mailboxId, id, index, string.IsNullOrEmpty(entry) ? null : entry, SessionKey(http, mailboxId), ct).ConfigureAwait(false) is not { Editable: true })
            {
                return Results.Text("{\"Hits\":[]}", "application/json");
            }
            // D-405: Find patient details belongs to Redact, which waits for chuvadi-pdf 3.35.1.
            if (svc.Viewer is not ViewerAccess { Switches.Redact: true } viewer)
            {
                return Results.Text("{\"Hits\":[]}", "application/json");
            }
            ViewResult r = await AskAsync(http, svc, words, id, index, new ViewRequest { Kind = ViewKinds.PdfSigns, Entry = string.IsNullOrEmpty(entry) ? null : entry, Switches = viewer.Switches }, ct).ConfigureAwait(false);
            return r.Ok && r.Answer.Json is string json ? Results.Text(json, "application/json") : Results.Text("{\"Hits\":[]}", "application/json");
        }).RequireAuthorization();

        app.MapGet(edit + "/{key}/signs", async (HttpContext http, Guid id, int index, string key, MailboxService svc, CancellationToken ct) =>
        {
            ViewResult r = await ArrangedAsync(http, svc, key, new ViewRequest { Kind = ViewKinds.PdfSigns }, ct).ConfigureAwait(false);
            return r.Ok && r.Answer.Json is string json ? Results.Text(json, "application/json") : Results.Text("{\"Hits\":[]}", "application/json");
        }).RequireAuthorization();

        app.MapPost(edit + "/save", async (HttpContext http, Guid id, int index, MailboxService svc, Words words, CancellationToken ct) =>
        {
            if (WebmailAuthService.MailboxIdOf(http.User) is not Guid mailboxId)
            {
                return Results.Unauthorized();
            }
            Lexicon l = words.For(WebmailAuthService.LanguageOf(http.User, words));
            IFormCollection form = await http.Request.ReadFormAsync(ct).ConfigureAwait(false);
            string? place = string.IsNullOrEmpty(form["entry"]) ? null : form["entry"].ToString();
            string how = form["go"].ToString();
            // The owner's check, 17 Nov 2026: the mail to answer or the draft to add to, chosen in the picker.
            Guid? chosen = Guid.TryParse(form["target"].ToString(), out Guid t) ? t : null;
            string session = SessionKey(http, mailboxId);
            // The second step after a redaction: the file the person has just read the summary of is attached as it is.
            string kept = form["kept"].ToString();
            if (kept.Length > 0)
            {
                if (!MailboxService.EditHandOns.Contains(how)
                    || await svc.EditTargetAsync(mailboxId, id, index, place, session, ct).ConfigureAwait(false) is not EditTarget { Editable: true } keptTarget)
                {
                    return Results.Json(new { problem = l["Something in the changes did not fit this file. Nothing was saved."] }, statusCode: 400);
                }
                if (svc.KeptEdited(session, id, index, place, kept) is not byte[] keptFile)
                {
                    return Results.Json(new { problem = l["The checked file is no longer kept. Save again."] }, statusCode: 409);
                }
                (Guid? keptDraft, string? keptProblem) = await svc.DraftWithEditedFileAsync(mailboxId, id, index, place, how, MailboxService.EditedName(keptTarget.Name), keptFile, chosen, ct).ConfigureAwait(false);
                return keptDraft is Guid kd
                    ? Results.Json(new { go = await GoneToAsync(svc, mailboxId, kd, how, place is null ? index : null, MailboxService.EditedName(keptTarget.Name), ct).ConfigureAwait(false) })
                    : Results.Json(new { problem = l[keptProblem ?? "The file could not be saved."] }, statusCode: 409);
            }
            if (MailboxService.ReadEditPlan(form["plan"]) is EditPlan { Redactions.Count: > 0 } && svc.Viewer is not ViewerAccess { Switches.Redact: true })
            {
                // D-405: redaction waits for chuvadi-pdf 3.35.1; the viewer refuses it too.
                return Results.Json(new { problem = l[EditWords(ViewStatus.Refused, new EditReport { Refused = EditRefusals.RedactOff })] }, statusCode: 409);
            }
            if (MailboxService.ReadEditPlan(form["plan"]) is not EditPlan plan || MailboxService.ReadJoin(form["join"]) is not List<int> joined
                || !MailboxService.EditHandOns.Contains(how) || form.Files.Count > MailboxService.MaxEditPictures
                || await svc.EditTargetAsync(mailboxId, id, index, place, session, ct).ConfigureAwait(false) is not EditTarget { Editable: true } target)
            {
                return Results.Json(new { problem = l["Something in the changes did not fit this file. Nothing was saved."] }, statusCode: 400);
            }
            var pictures = new List<byte[]>();
            foreach (IFormFile f in form.Files)
            {
                if (f.Length > MailboxService.MaxEditPictureBytes)
                {
                    return Results.Json(new { problem = l["A picture is larger than 8 MB."] }, statusCode: 400);
                }
                using var copy = new MemoryStream();
                await f.CopyToAsync(copy, ct).ConfigureAwait(false);
                pictures.Add(copy.ToArray());
            }
            EditSaved saved = await svc.SaveEditAsync(mailboxId, id, index, place, plan, joined, pictures, session, ct).ConfigureAwait(false);
            if (saved.File is not byte[] file)
            {
                return Results.Json(new { problem = l[EditWords(saved.Status, saved.Report)].Replace("{n}", ((saved.Report?.SurvivorPage ?? 0) + 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal), page = saved.Report?.SurvivorPage, box = saved.Report?.SurvivorBox }, statusCode: 409);
            }
            // Decisions of 15 Nov 2026 (3.34.0 ApplyWithReport): after a redaction the person reads what was removed
            // before the file is attached; the file waits in this session's memory, made once.
            if (plan.Redactions.Count > 0)
            {
                return svc.KeepEdited(session, id, index, place, file) is string key
                    ? Results.Json(new { review = RedactionReview(l, saved.Report), kept = key })
                    : Results.Json(new { problem = l["The viewer is busy or not running. Try again in a minute."] }, statusCode: 409);
            }
            (Guid? draft, string? problem) = await svc.DraftWithEditedFileAsync(mailboxId, id, index, place, how, MailboxService.EditedName(target.Name), file, chosen, ct).ConfigureAwait(false);
            return draft is Guid d
                ? Results.Json(new { go = await GoneToAsync(svc, mailboxId, d, how, place is null ? index : null, MailboxService.EditedName(target.Name), ct).ConfigureAwait(false) })
                : Results.Json(new { problem = l[problem ?? "The file could not be saved."] }, statusCode: 409);
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);
    }

    // A folder as the rail names it: the standard folders in the person's language, their own folders as named.
    private static string FolderWord(Lexicon l, string folder) => folder switch
    {
        "INBOX" => l["Inbox"],
        "Sent" => l["Sent"],
        "Archive" => l["Archive"],
        "Drafts" => l["Drafts"],
        _ => folder,
    };

    // Where Save goes: the draft; for a file put back on a draft, that draft with the new file open in the viewer -
    // in the place of the file it replaced, or (a PDF from a ZIP, added beside it) found by its name.
    private static async Task<string> GoneToAsync(MailboxService svc, Guid mailboxId, Guid draft, string how, int? replaced, string name, CancellationToken ct)
    {
        string go = "/draft/" + draft.ToString("D");
        if (how != "ondraft")
        {
            return go;
        }
        int? k = replaced ?? await svc.DraftFileIndexAsync(mailboxId, draft, name, ct).ConfigureAwait(false);
        return k is int at ? go + "?view=" + at.ToString(CultureInfo.InvariantCulture) : go;
    }

    // The owner's check, 17 Nov 2026: the mails the picker offers - to answer with an edited file, or to turn a draft
    // into a reply - and the drafts an edited file can be added to. The words come from the person; nothing changes.
    internal static void MapPicker(WebApplication app)
    {
        app.MapGet("/mail/pick", async (HttpContext http, MailboxService svc, Words words, CancellationToken ct) =>
        {
            if (WebmailAuthService.MailboxIdOf(http.User) is not Guid mailboxId)
            {
                return Results.Unauthorized();
            }
            ZonedClock clock = WebmailAuthService.ClockOf(http.User);
            Lexicon l = words.For(WebmailAuthService.LanguageOf(http.User, words));
            bool drafts = http.Request.Query["drafts"].ToString() == "1";
            Guid? except = Guid.TryParse(http.Request.Query["except"].ToString(), out Guid e) ? e : null;
            IReadOnlyList<PickItem> items = await svc.PickAsync(mailboxId, http.Request.Query["q"].ToString(), drafts, except, ct).ConfigureAwait(false);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            http.Response.Headers.CacheControl = "no-store";
            return Results.Json(items.Select(i => new { id = i.Id.ToString("D"), who = i.Who.Length > 0 ? i.Who : l["(no recipient)"], subject = i.Subject.Length > 0 ? i.Subject : l["(no subject)"], when = clock.List(i.At, now), folder = FolderWord(l, i.Folder) }));
        }).RequireAuthorization();
    }

    // DES-22 §7.5, §6.2: the person's own saved signature - read for the pad, kept only when they ask, removed in
    // Settings. Sealed to their key; never shown to anyone else.
    internal static void MapSignature(WebApplication app)
    {
        app.MapGet("/settings/drawn-signature", async (HttpContext http, MailboxService svc, CancellationToken ct) =>
        {
            if (WebmailAuthService.MailboxIdOf(http.User) is not Guid mailboxId)
            {
                return Results.Unauthorized();
            }
            http.Response.Headers.CacheControl = "no-store";
            // In the case it was kept in (Points, Strokes, W, H), as the editor's script reads it.
            return await svc.DrawnSignatureAsync(mailboxId, ct).ConfigureAwait(false) is DrawnSignature s
                ? Results.Json(s, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.General))
                : Results.NoContent();
        }).RequireAuthorization();

        app.MapPost("/settings/drawn-signature", async (HttpContext http, [FromForm] string? signature, MailboxService svc, CancellationToken ct) =>
        {
            if (WebmailAuthService.MailboxIdOf(http.User) is not Guid mailboxId)
            {
                return Results.Unauthorized();
            }
            DrawnSignature? s = null;
            try
            {
                s = string.IsNullOrEmpty(signature) || signature.Length > 400_000 ? null : System.Text.Json.JsonSerializer.Deserialize<DrawnSignature>(signature);
            }
            catch (System.Text.Json.JsonException)
            {
                s = null;
            }
            return s is not null && await svc.SaveDrawnSignatureAsync(mailboxId, s, ct).ConfigureAwait(false) ? Results.NoContent() : Results.BadRequest();
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        app.MapPost("/settings/drawn-signature/remove", async (HttpContext http, MailboxService svc, CancellationToken ct) =>
        {
            if (WebmailAuthService.MailboxIdOf(http.User) is not Guid mailboxId)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.RemoveDrawnSignatureAsync(mailboxId, ct).ConfigureAwait(false);
            return Results.Redirect("/settings/mail?saved=drawnremoved");
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);
    }

    // What the redaction removed, page by page, and the pages where a box covered nothing (a note, not a refusal).
    internal static List<string> RedactionReview(Lexicon l, EditReport? report)
    {
        ArgumentNullException.ThrowIfNull(l);
        var lines = new List<string>();
        foreach (RedactedPage p in report?.Removed ?? new List<RedactedPage>())
        {
            var what = new List<string>();
            if (p.Characters > 0)
            {
                what.Add(p.Characters == 1 ? l["1 character"] : Count(l["{n} characters"], p.Characters));
            }
            if (p.Pictures > 0)
            {
                what.Add(p.Pictures == 1 ? l["1 picture"] : Count(l["{n} pictures"], p.Pictures));
            }
            if (p.Drawings > 0)
            {
                what.Add(p.Drawings == 1 ? l["1 drawing"] : Count(l["{n} drawings"], p.Drawings));
            }
            if (p.FormValues > 0)
            {
                what.Add(p.FormValues == 1 ? l["1 form value"] : Count(l["{n} form values"], p.FormValues));
            }
            lines.Add(Count(l["Removed on page {n}: {what}."], p.Page + 1).Replace("{what}", string.Join(", ", what), StringComparison.Ordinal));
        }
        foreach (int page in report?.NothingUnder ?? new List<int>())
        {
            lines.Add(Count(l["Nothing was under the box on page {n}."], page + 1));
        }
        return lines;
    }

    private static string Count(string words, int n) => words.Replace("{n}", n.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    // The words for a save or arrange that did not go through (DES-22 §7.6, §7.8): never the file's content.
    internal static string EditWords(string status, EditReport? report) => report?.Refused switch
    {
        EditRefusals.Survivor => "Something still shows inside a redaction box on page {n}. Nothing was saved.",
        EditRefusals.DigitallySigned => "This PDF is digitally signed. Tick “I understand” to save a changed copy.",
        EditRefusals.Password => "This PDF has a password. Tick that the new file will have no password to save it.",
        EditRefusals.Skipped or EditRefusals.Leftovers => "The redacted file could not be cleared of its bookmarks and details, so nothing was saved.",
        EditRefusals.Reopen => "The new file did not open again, so it was not saved.",
        EditRefusals.Plan => "Something in the changes did not fit this file. Nothing was saved.",
        EditRefusals.RedactOff => "Redacting is not available yet. Nothing was saved.",
        EditRefusals.Layers => "This file, or one joined to it, has hidden layers, so its pages cannot be changed here yet. Nothing was saved.",
        _ => status switch
        {
            ViewStatus.TooLarge => "The file is too large to save.",
            ViewStatus.TooSlow => "Saving took too long. Try fewer changes at once.",
            ViewStatus.Unavailable or ViewStatus.Busy => "The viewer is busy or not running. Try again in a minute.",
            ViewStatus.NeedsPassword or ViewStatus.WrongPassword => "Open the file with its password first.",
            _ => "The file could not be saved.",
        },
    };

    private static async Task<ViewResult> ArrangedAsync(HttpContext http, MailboxService svc, string key, ViewRequest request, CancellationToken ct) =>
        WebmailAuthService.MailboxIdOf(http.User) is Guid mailboxId
            ? await svc.ArrangedViewAsync(key, request, SessionKey(http, mailboxId), ct).ConfigureAwait(false)
            : ViewResult.Of(ViewStatus.Unavailable);

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
