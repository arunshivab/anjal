using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anjal.Viewing;

namespace Anjal.Webmail.Services;

/// <summary>What the editor works on: the file's name, and whether it may be edited at all.</summary>
/// <param name="Name">The file's name, as the new file is named after it.</param>
/// <param name="Editable">A PDF the person may edit: not from a locked archive (AC-20), the viewer on.</param>
public sealed record EditTarget(string Name, bool Editable);

/// <summary>The outcome of Save: the new file and its report, or a refusal and its reason.</summary>
/// <param name="File">The new PDF, or null.</param>
/// <param name="Report">What Save did, or why it was refused.</param>
/// <param name="Status">The viewer's status (<see cref="ViewStatus"/>).</param>
public sealed record EditSaved(byte[]? File, EditReport? Report, string Status);

/// <summary>
/// rc.29 part 2 (DES-22 §7): the PDF editor's side of the webmail. The original attachment is only read; the
/// pages as they will be are kept in the session's memory like a viewed file's pictures (D-362); Save makes a new
/// file on a new draft - a reply, a reply to all, a forward, or a draft of its own (§6.3, D-382).
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The most PDFs of the same mail one result may join (D-371).</summary>
    public const int MaxJoined = 16;

    /// <summary>The most pictures (and scanned signatures) one save may carry.</summary>
    public const int MaxEditPictures = 16;

    /// <summary>The largest picture a mark may use.</summary>
    public const long MaxEditPictureBytes = 8L * 1024 * 1024;

    /// <summary>The ways Save hands the new file on (§7.2).</summary>
    public static readonly IReadOnlySet<string> EditHandOns = new HashSet<string>(StringComparer.Ordinal) { "reply", "replyall", "forward", "draft", "replyto", "replyallto", "adddraft", "ondraft" };

    /// <summary>The file the editor would work on, judged as the viewer judges it; null when it is not a PDF here.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The mail.</param>
    /// <param name="index">The attachment.</param>
    /// <param name="entry">The place inside its ZIP, or null.</param>
    /// <param name="session">The signed-in session.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The target, or null.</returns>
    public async Task<EditTarget?> EditTargetAsync(Guid mailboxId, Guid messageId, int index, string? entry, string session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (this.Viewer is not ViewerAccess viewer || !viewer.Enabled)
        {
            return null;
        }
        IReadOnlyList<AttachmentView>? files = await this.ListAttachmentsAsync(mailboxId, messageId, ct).ConfigureAwait(false);
        AttachmentView? file = files?.FirstOrDefault(a => a.Index == index);
        if (file is null)
        {
            return null;
        }
        string? kind = PreviewType(file);
        if (string.IsNullOrEmpty(entry))
        {
            return kind == "application/pdf" ? new EditTarget(file.FileName, true) : null;
        }
        if (kind != ZipType)
        {
            return null;
        }
        int[] places = ViewerLinks.Places(entry);
        if (places.Length == 0)
        {
            return null;
        }
        string? holder = places.Length > 1 ? places[0].ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        ViewResult list = await this.ViewAsync(mailboxId, messageId, index, new ViewRequest { Kind = ViewKinds.ArchiveList, Entry = holder }, session, null, ct).ConfigureAwait(false);
        ArchiveView? parent = list.Ok ? ViewJson.ReadArchive(list.Answer.Json) : null;
        ArchiveItem? item = parent?.Entries.FirstOrDefault(e => e.Index == places[^1]);
        return item is null || item.Blocked || item.Folder || item.Opens != FileKinds.Pdf
            ? null
            // AC-20: a file from a locked archive was not virus-scanned; it is shown, never given out, so never edited.
            : new EditTarget(item.Name, !parent!.Locked);
    }

    /// <summary>What the editor must know before it opens: signatures, a password, forms (§7.8, 1).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The mail.</param>
    /// <param name="index">The attachment.</param>
    /// <param name="entry">The place inside its ZIP, or null.</param>
    /// <param name="session">The signed-in session.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The check, or null with the viewer's status.</returns>
    public async Task<(EditCheck? Check, string Status)> EditCheckAsync(Guid mailboxId, Guid messageId, int index, string? entry, string session, CancellationToken ct = default)
    {
        ViewResult r = await this.ViewAsync(mailboxId, messageId, index, new ViewRequest { Kind = ViewKinds.PdfEditCheck, Entry = entry }, session, null, ct).ConfigureAwait(false);
        return (r.Ok ? ViewJson.ReadEditCheck(r.Answer.Json) : null, r.Answer.Status);
    }

    /// <summary>
    /// The pages in their new order and turn, joined with the mail's other PDFs: made by the viewer, kept in the
    /// session's memory under a key, and described (its pages' sizes) for the editor to show.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The mail.</param>
    /// <param name="index">The attachment.</param>
    /// <param name="entry">The place inside its ZIP, or null.</param>
    /// <param name="pages">The pages as they will be.</param>
    /// <param name="join">The mail's other PDFs joined, by attachment number, in their order.</param>
    /// <param name="session">The signed-in session.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The key, the arranged file's description and the pages as picked (a whole file's "all pages", page -1,
    /// spelled out), or null with the viewer's status.</returns>
    public async Task<(string? Key, PdfInfo? Info, List<PagePick>? Picks, string Status)> ArrangeEditAsync(Guid mailboxId, Guid messageId, int index, string? entry, IReadOnlyList<PagePick> pages, IReadOnlyList<int> join, string session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(join);
        ArgumentNullException.ThrowIfNull(session);
        if (this.Viewer is not ViewerAccess viewer)
        {
            return (null, null, null, ViewStatus.Unavailable);
        }
        // A joined file's pages, asked for as page -1, are every page of that file in order.
        var picks = new List<PagePick>();
        foreach (PagePick p in pages)
        {
            if (p.Page != -1)
            {
                picks.Add(p);
                continue;
            }
            if (p.File < 0 || p.File > join.Count)
            {
                return (null, null, null, ViewStatus.Refused);
            }
            ViewResult counted = p.File == 0
                ? await this.ViewAsync(mailboxId, messageId, index, new ViewRequest { Kind = ViewKinds.PdfInfo, Entry = entry }, session, null, ct).ConfigureAwait(false)
                : await this.ViewAsync(mailboxId, messageId, join[p.File - 1], new ViewRequest { Kind = ViewKinds.PdfInfo }, session, null, ct).ConfigureAwait(false);
            if (!counted.Ok || ViewJson.ReadPdfInfo(counted.Answer.Json) is not PdfInfo whole)
            {
                return (null, null, null, counted.Answer.Status);
            }
            picks.AddRange(Enumerable.Range(0, whole.Pages.Count).Select(n => new PagePick { File = p.File, Page = n, Turn = p.Turn }));
        }
        EditPlan plan = new() { Pages = picks };
        string key = ArrangedKey(mailboxId, messageId, index, entry, plan, join);
        if (viewer.KeptFile(session, key) is null)
        {
            (ViewRequest request, List<byte[]>? files, string status) = await this.EditRequestAsync(mailboxId, messageId, index, entry, ViewKinds.PdfArrange, plan, join, Array.Empty<byte[]>(), session, ct).ConfigureAwait(false);
            if (files is null)
            {
                return (null, null, null, status);
            }
            ViewResult made = await viewer.AskOnceAsync(request, files, ct).ConfigureAwait(false);
            if (!made.Ok || made.Files.Count == 0)
            {
                // D-406: a file with hidden layers keeps its pages until chuvadi-pdf 3.35.1; the editor says so.
                return (null, null, null, ViewJson.ReadEditReport(made.Answer.Json)?.Refused == EditRefusals.Layers ? EditRefusals.Layers : made.Answer.Status);
            }
            viewer.KeepFile(session, key, made.Files[0]);
        }
        ViewResult info = await this.ArrangedViewAsync(key, new ViewRequest { Kind = ViewKinds.PdfInfo }, session, ct).ConfigureAwait(false);
        return info.Ok ? (key, ViewJson.ReadPdfInfo(info.Answer.Json), picks, ViewStatus.Ok) : (null, null, null, info.Answer.Status);
    }

    /// <summary>One of the viewer's jobs (a page, its text, Find) run on the arranged pages kept under a key.</summary>
    /// <param name="key">The key <see cref="ArrangeEditAsync"/> gave.</param>
    /// <param name="request">The job.</param>
    /// <param name="session">The signed-in session.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The answer; Failed when the arranged file has gone from memory (the editor arranges again).</returns>
    public async Task<ViewResult> ArrangedViewAsync(string key, ViewRequest request, string session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);
        if (this.Viewer is not ViewerAccess viewer || !IsArrangedKey(key))
        {
            return ViewResult.Of(ViewStatus.Unavailable);
        }
        // The arranged file is Anjal's own sanitized copy: it has no password and lives only in this session's memory.
        return await viewer.AskAsync(session, "arranged/" + key, new ViewRequest { Kind = request.Kind, Page = request.Page, Width = request.Width, Query = request.Query, Switches = viewer.Switches }, () => Task.FromResult(viewer.KeptFile(session, key)), ct).ConfigureAwait(false);
    }

    /// <summary>Applies the whole plan in the viewer, which checks the result (§7.8), and gives back the new PDF or the refusal.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The mail.</param>
    /// <param name="index">The attachment.</param>
    /// <param name="entry">The place inside its ZIP, or null.</param>
    /// <param name="plan">The plan, as the page sent it (its joined passwords are never the page's: they are filled here).</param>
    /// <param name="join">The mail's other PDFs joined, by attachment number.</param>
    /// <param name="pictures">The pictures its marks use.</param>
    /// <param name="session">The signed-in session.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async Task<EditSaved> SaveEditAsync(Guid mailboxId, Guid messageId, int index, string? entry, EditPlan plan, IReadOnlyList<int> join, IReadOnlyList<byte[]> pictures, string session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(join);
        ArgumentNullException.ThrowIfNull(pictures);
        ArgumentNullException.ThrowIfNull(session);
        if (this.Viewer is not ViewerAccess viewer)
        {
            return new EditSaved(null, null, ViewStatus.Unavailable);
        }
        if (pictures.Count > MaxEditPictures || pictures.Any(p => p.LongLength > MaxEditPictureBytes))
        {
            return new EditSaved(null, new EditReport { Refused = EditRefusals.Plan }, ViewStatus.TooLarge);
        }
        (ViewRequest request, List<byte[]>? files, string status) = await this.EditRequestAsync(mailboxId, messageId, index, entry, ViewKinds.PdfSave, plan, join, pictures, session, ct).ConfigureAwait(false);
        if (files is null)
        {
            return new EditSaved(null, null, status);
        }
        ViewResult r = await viewer.AskOnceAsync(request, files, ct).ConfigureAwait(false);
        EditReport? report = ViewJson.ReadEditReport(r.Answer.Json);
        return r.Ok && r.Files.Count > 0 ? new EditSaved(r.Files[0], report, ViewStatus.Ok) : new EditSaved(null, report, r.Answer.Status);
    }

    /// <summary>
    /// The new file on a new draft (§7.2): a reply, a reply to all, or a forward of the mail (carrying its other
    /// attachments), or a new mail of its own. The owner's check (17 Nov 2026) added: a reply or a reply to all to
    /// another mail (<paramref name="target"/>), the file added to a draft the person is writing (the target), and,
    /// for a file on a draft, the file put back on that draft in its place. The original mail and its attachments
    /// are never changed (§6.3); only a draft is ever rewritten.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The mail the file came from.</param>
    /// <param name="index">The attachment that was edited (a forward carries the others; a ZIP is carried whole, D-382).</param>
    /// <param name="entry">The place inside its ZIP, or null.</param>
    /// <param name="how">reply, replyall, forward, draft (a new mail), replyto, replyallto, adddraft or ondraft.</param>
    /// <param name="fileName">The new file's name.</param>
    /// <param name="file">The new file.</param>
    /// <param name="target">For replyto and replyallto the mail to answer; for adddraft the draft; otherwise null.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The draft, or null with the words to show.</returns>
    public async Task<(Guid? Draft, string? Problem)> DraftWithEditedFileAsync(Guid mailboxId, Guid messageId, int index, string? entry, string how, string fileName, byte[] file, Guid? target = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(how);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(file);
        if (!EditHandOns.Contains(how) || (how is "replyto" or "replyallto" or "adddraft") != (target is not null))
        {
            return (null, "The file could not be saved.");
        }
        if (how == "adddraft")
        {
            return await this.AddToDraftAsync(mailboxId, target!.Value, fileName, file, ct).ConfigureAwait(false);
        }
        if (how == "ondraft")
        {
            // A file on a draft: back in its own place; a PDF taken from a ZIP on the draft goes beside the ZIP.
            return string.IsNullOrEmpty(entry)
                ? await this.ReplaceOnDraftAsync(mailboxId, messageId, index, fileName, file, ct).ConfigureAwait(false)
                : await this.AddToDraftAsync(mailboxId, messageId, fileName, file, ct).ConfigureAwait(false);
        }
        Guid source = messageId;
        if (how is "replyto" or "replyallto")
        {
            if (await this.ReadRawAsync(mailboxId, target!.Value, ct).ConfigureAwait(false) is not { } chosen || NotAnswerable.Contains(chosen.Folder.Name))
            {
                return (null, "The file could not be saved.");
            }
            source = target.Value;
            how = how == "replyto" ? "reply" : "replyall";
        }
        var request = new ComposeRequest();
        if (how == "draft")
        {
            request.Subject = Path.GetFileNameWithoutExtension(fileName);
            (request.Body, request.BodyHtml) = await this.StartBodyAsync(mailboxId, string.Empty, ct).ConfigureAwait(false);
        }
        else
        {
            PrefillKind kind = how switch { "reply" => PrefillKind.Reply, "replyall" => PrefillKind.ReplyAll, _ => PrefillKind.Forward };
            if (await this.PrefillAsync(mailboxId, source, kind, ct).ConfigureAwait(false) is not ComposePrefill prefill)
            {
                return (null, "The file could not be saved.");
            }
            request.To = prefill.To;
            request.Cc = prefill.Cc;
            request.Subject = prefill.Subject;
            request.InReplyTo = prefill.InReplyTo;
            request.References = prefill.References;
            (request.Body, request.BodyHtml) = await this.StartBodyAsync(mailboxId, prefill.Body, ct).ConfigureAwait(false);
            if (how == "forward")
            {
                // The mail's other files go with the forward; the edited one goes as its new self (a ZIP whole, D-382).
                request.CarryFrom = messageId;
                foreach (AttachmentView a in await this.ListAttachmentsAsync(mailboxId, messageId, ct).ConfigureAwait(false) ?? Array.Empty<AttachmentView>())
                {
                    if (a.Index != index || !string.IsNullOrEmpty(entry))
                    {
                        request.CarryIndexes.Add(a.Index);
                    }
                }
            }
        }
        request.Attachments.Add((fileName, "application/pdf", file));
        if (await this.AddCarriedAttachmentsAsync(mailboxId, request, ct).ConfigureAwait(false) is string tooMuch)
        {
            return (null, tooMuch);
        }
        request.CarryFrom = null;
        request.CarryIndexes.Clear();
        Guid? draft = await this.SaveDraftAsync(mailboxId, request, ct).ConfigureAwait(false);
        return draft is Guid id ? (id, null) : (null, "The file could not be saved.");
    }

    /// <summary>The new file's name (§7.2): "name (edited).pdf", kept to a safe file name; a file already so named keeps its name.</summary>
    /// <param name="original">The original's name.</param>
    /// <returns>The new name.</returns>
    public static string EditedName(string original)
    {
        string stem = Path.GetFileNameWithoutExtension((original ?? string.Empty).Replace('\\', '/').Split('/')[^1]);
        stem = new string(stem.Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>' or '|' or ':' or '*' or '?')).ToArray()).Trim();
        if (stem.Length == 0)
        {
            stem = "document";
        }
        if (stem.Length > 120)
        {
            stem = stem[..120];
        }
        // A file edited again keeps one mark (the owner's check, 17 Nov 2026: a draft's file can be edited again).
        return stem.EndsWith(" (edited)", StringComparison.Ordinal) ? stem + ".pdf" : stem + " (edited).pdf";
    }

    // The request for an editor job and its files: the attachment (the viewer takes the PDF from its ZIP), the
    // joined PDFs of the same mail (D-371) with their passwords from this session, then the pictures. The page's
    // plan is copied with the server's own count and passwords, never the page's.
    private async Task<(ViewRequest Request, List<byte[]>? Files, string Status)> EditRequestAsync(Guid mailboxId, Guid messageId, int index, string? entry, string kind, EditPlan plan, IReadOnlyList<int> join, IReadOnlyList<byte[]> pictures, string session, CancellationToken ct)
    {
        var empty = new ViewRequest();
        if (this.Viewer is not ViewerAccess viewer)
        {
            return (empty, null, ViewStatus.Unavailable);
        }
        if (join.Count > MaxJoined || join.Contains(index) && string.IsNullOrEmpty(entry) || join.Distinct().Count() != join.Count)
        {
            return (empty, null, ViewStatus.Refused);
        }
        (AttachmentView View, byte[] Bytes)? main = await this.GetAttachmentAsync(mailboxId, messageId, index, ct).ConfigureAwait(false);
        if (main is null)
        {
            return (empty, null, ViewStatus.Failed);
        }
        var files = new List<byte[]> { main.Value.Bytes };
        var passwords = new List<string?>();
        foreach (int j in join)
        {
            (AttachmentView View, byte[] Bytes)? other = await this.GetAttachmentAsync(mailboxId, messageId, j, ct).ConfigureAwait(false);
            if (other is null || PreviewType(other.Value.View) != "application/pdf")
            {
                return (empty, null, ViewStatus.Refused);
            }
            files.Add(other.Value.Bytes);
            passwords.Add(viewer.PasswordFor(session, FileKey(mailboxId, messageId, j, null)));
        }
        files.AddRange(pictures);
        var server = new EditPlan
        {
            Pages = plan.Pages,
            Joined = join.Count,
            JoinedPasswords = passwords,
            Marks = plan.Marks,
            Redactions = plan.Redactions,
            Watermark = plan.Watermark,
            Numbers = plan.Numbers,
            AcceptSignedRewrite = plan.AcceptSignedRewrite,
            AcceptNoPassword = plan.AcceptNoPassword,
        };
        string fileKey = FileKey(mailboxId, messageId, index, entry);
        ViewRequest request = ViewerAccess.With(new ViewRequest { Kind = kind, Entry = entry, Plan = server, Switches = viewer.Switches }, viewer.PasswordFor(session, fileKey), viewer.WhoKey(mailboxId), null);
        if (!string.IsNullOrEmpty(entry))
        {
            string[] places = entry.Split('/');
            request = ViewerAccess.WithArchive(
                request,
                viewer.PasswordFor(session, FileKey(mailboxId, messageId, index, null)),
                places.Length > 1 ? viewer.PasswordFor(session, FileKey(mailboxId, messageId, index, places[0])) : null);
        }
        return (request, files, ViewStatus.Ok);
    }

    // The arranged file's key: what it was made from, hashed (the mailbox and mail in it, so no other session's key
    // could ever name it, and it is kept in this session's memory only).
    private static string ArrangedKey(Guid mailboxId, Guid messageId, int index, string? entry, EditPlan plan, IReadOnlyList<int> join)
    {
        string made = FileKey(mailboxId, messageId, index, entry) + "|" + ViewJson.Write(plan) + "|" + string.Join(',', join);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(made)))[..32].ToLowerInvariant();
    }

    /// <summary>
    /// Keeps a redacted file the viewer made, for 10 minutes in this session's memory, so the person reads what the
    /// redaction removed before it is attached (decisions of 15 Nov 2026) and the file is not made twice.
    /// </summary>
    /// <param name="session">The signed-in session.</param>
    /// <param name="messageId">The mail.</param>
    /// <param name="index">The attachment.</param>
    /// <param name="entry">The place inside its ZIP, or null.</param>
    /// <param name="file">The redacted file.</param>
    /// <returns>The key the page sends back to attach it; null when the viewer is not running.</returns>
    public string? KeepEdited(string session, Guid messageId, int index, string? entry, byte[] file)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(file);
        if (this.Viewer is not ViewerAccess viewer)
        {
            return null;
        }
        string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        viewer.KeepFile(session, EditedKey(messageId, index, entry, key), file);
        return key;
    }

    /// <summary>A redacted file kept by <see cref="KeepEdited"/> for this same file, or null when it has gone.</summary>
    /// <param name="session">The signed-in session.</param>
    /// <param name="messageId">The mail.</param>
    /// <param name="index">The attachment.</param>
    /// <param name="entry">The place inside its ZIP, or null.</param>
    /// <param name="key">The key the page sent.</param>
    /// <returns>The file, or null.</returns>
    public byte[]? KeptEdited(string session, Guid messageId, int index, string? entry, string key)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(key);
        return this.Viewer is ViewerAccess viewer && IsArrangedKey(key) ? viewer.KeptFile(session, EditedKey(messageId, index, entry, key)) : null;
    }

    // Kept for one file only: a key from another mail or attachment finds nothing.
    private static string EditedKey(Guid messageId, int index, string? entry, string key) =>
        "edited/" + messageId.ToString("N") + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + (entry ?? string.Empty) + "/" + key;

    private static bool IsArrangedKey(string key) => key.Length == 32 && key.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>Reads an editor plan the page sent; null when it is not one.</summary>
    /// <param name="json">The page's JSON.</param>
    /// <returns>The plan, or null.</returns>
    public static EditPlan? ReadEditPlan(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > ViewFrames.MaxJsonBytes / 2)
        {
            return null;
        }
        try
        {
            return ViewJson.ReadPlan(json)?.Whole();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads the attachment numbers of the PDFs to join ("2,4"); null when the list is not one.</summary>
    /// <param name="text">The list.</param>
    /// <returns>The numbers, or null.</returns>
    public static List<int>? ReadJoin(string? text)
    {
        var all = new List<int>();
        foreach (string part in (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) || n > 1000)
            {
                return null;
            }
            all.Add(n);
        }
        return all.Count > MaxJoined ? null : all;
    }
}
