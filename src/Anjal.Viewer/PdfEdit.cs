using System.Globalization;
using Anjal.Viewing;
using Chuvadi.Pdf.Authoring;
using Chuvadi.Pdf.Documents;
using Chuvadi.Pdf.Forms;
using Chuvadi.Pdf.Graphics;
using Chuvadi.Pdf.Operations;
using Chuvadi.Pdf.Primitives;
using Chuvadi.Pdf.Redaction;
using Chuvadi.Pdf.Rendering;
using Chuvadi.Pdf.Signatures;
using Chuvadi.Pdf.Signatures.Verification;
using AuthoringPageSize = Chuvadi.Pdf.Authoring.PageSize;
using Color = Chuvadi.Pdf.Authoring.Color;
using Path = Chuvadi.Pdf.Graphics.Path;

namespace Anjal.Viewer;

/// <summary>
/// rc.29 part 2 (DES-22 §7): the PDF editor's work in the viewer's child process. The original is only read; every
/// result is a new file. Save applies the plan in one pass and in DES-22's order - arrange (with join's flattening),
/// marks, signatures, watermark and page numbers (one overlay), then redaction (apply, verify, sanitize, no outline,
/// the compressor last) or, when nothing was redacted, the sanitizer last - and then opens the result again and
/// draws its first page. Every write runs inside <see cref="PdfWorkScope"/> (AC-11); the child's deadline and
/// memory cap remain the real guarantee.
/// </summary>
public static class PdfEdit
{
    /// <summary>The most marks one plan may carry.</summary>
    public const int MaxMarks = 2000;

    /// <summary>The most points one mark may carry (a long signature or pen stroke).</summary>
    public const int MaxPoints = 20000;

    /// <summary>The most pages the result may have.</summary>
    public const int MaxPages = 2000;

    /// <summary>The longest text a text box, watermark or page-number line may hold.</summary>
    public const int MaxText = 2000;

    /// <summary>Pad each redaction box by this much on every side (DES-22 §7.6: the boxes come from font metrics).</summary>
    public const double FindPadding = 2;

    private const string Lipi = "Lipi";

    private static readonly SanitizeOptions Disarm = new()
    {
        RemoveEmbeddedFiles = true,
        RemoveXfa = true,
        RemoveMultimedia = true,
        KeepUriLinks = true,
        AllowEncryptedInput = true,
    };

    /// <summary>What the editor must know before it opens: signatures, a password, forms, active content (§7.8, 1).</summary>
    /// <param name="bytes">The file.</param>
    /// <param name="password">Its password, or null.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The answer.</returns>
    public static JobAnswer Check(byte[] bytes, string? password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Guard("check", () =>
        {
            using PdfWorkScope scope = PdfWorkScope.Enter(PdfJobs.Limits, ct);
            using PdfDocument doc = Open(bytes, password, out JobAnswer? refused);
            if (refused is not null)
            {
                return refused;
            }
            var check = new EditCheck
            {
                Signatures = SignaturesOf(doc),
                Locked = doc.Encryption is not null,
                HasForm = HasForm(doc),
                Active = PdfJobs.ActiveContent(doc),
                PageCount = doc.PageCount,
                HasLayers = HasLayers(doc),
            };
            return JobAnswer.Json(ViewJson.Write(check));
        });
    }

    /// <summary>
    /// The pages in their new order and turn, as a PDF: the viewer's own jobs then show it, find in it and give its
    /// text, so the person edits the pages as they will be. Nothing else of the plan is applied.
    /// </summary>
    /// <param name="files">The file being edited first, then the joined PDFs.</param>
    /// <param name="password">The first file's password, or null.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="switches">The held-back parts the webmail allows (D-405, D-406).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The answer, with the arranged PDF as its one file.</returns>
    public static JobAnswer Arrange(IReadOnlyList<byte[]> files, string? password, EditPlan plan, EditSwitches switches, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(switches);
        return Guard("arrange", () =>
        {
            using PdfWorkScope scope = PdfWorkScope.Enter(PdfJobs.Limits, ct);
            using var work = new Work();
            if (!work.OpenSources(files, password, plan, out JobAnswer? refused))
            {
                return refused!;
            }
            if (HeldBack(work, plan, switches) is JobAnswer held)
            {
                return held;
            }
            if (Arranged(work, plan) is not byte[] arranged)
            {
                return Refuse(EditRefusals.Plan);
            }
            return JobAnswer.File(arranged, null);
        });
    }

    /// <summary>Applies the whole plan, checks the result, and gives back the new PDF with a report (§7.1, §7.8).</summary>
    /// <param name="files">The file being edited, then the joined PDFs, then the pictures the marks use.</param>
    /// <param name="password">The first file's password, or null.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="switches">The held-back parts the webmail allows (D-405, D-406).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The answer: the new PDF and its report, or a refusal with its reason.</returns>
    public static JobAnswer Save(IReadOnlyList<byte[]> files, string? password, EditPlan plan, EditSwitches switches, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(switches);
        return Guard("save", () =>
        {
            using PdfWorkScope scope = PdfWorkScope.Enter(PdfJobs.Limits, ct);
            using var work = new Work();
            if (!work.OpenSources(files, password, plan, out JobAnswer? refused))
            {
                return refused!;
            }
            if (HeldBack(work, plan, switches) is JobAnswer held)
            {
                return held;
            }
            PdfDocument main = work.Sources[0];
            // §7.8, 1 and D-374: the person was told, and ticked, before anything is written.
            if (SignaturesOf(main).Count > 0 && !plan.AcceptSignedRewrite)
            {
                return Refuse(EditRefusals.DigitallySigned);
            }
            if (work.Sources.Any(d => d.Encryption is not null) && !plan.AcceptNoPassword)
            {
                return Refuse(EditRefusals.Password);
            }

            // What the original holds that no file Anjal makes may carry (the report counts it; the sanitizer removes it).
            SanitizeReport found = PdfSanitizer.Inspect(main, Disarm);
            int activeRemoved = found.ActionsRemoved + found.AdditionalActionsRemoved + found.JavaScriptTreesRemoved + found.AnnotationsRemoved + (found.XfaRemoved ? 1 : 0);
            int filesRemoved = found.EmbeddedFilesRemoved;

            // 1. Arrange (join flattens every input first, AC-10).
            if (Arranged(work, plan) is not byte[] arranged)
            {
                return Refuse(EditRefusals.Plan);
            }
            PdfDocument doc = work.Reopen(arranged);

            // 2. Marks, signatures, watermark and page numbers: one overlay, one PlaceMany (§7.4, §7.7).
            IReadOnlyList<byte[]> pictures = files.Skip(1 + plan.Joined).ToList();
            if (!PlanFits(plan, doc.PageCount, pictures))
            {
                return Refuse(EditRefusals.Plan);
            }
            if (plan.Marks.Count > 0 || plan.Watermark is not null || plan.Numbers is not null)
            {
                doc = work.Reopen(Overlay(work, doc, plan, pictures));
            }

            bool outlineRemoved = false;
            List<RedactedPage> removed = new();
            List<int> nothingUnder = new();
            byte[] final;
            if (plan.Redactions.Count > 0)
            {
                // 3. Redaction (§7.6, D-375): everything flattened first, so nothing can be lifted off the page.
                doc = work.Reopen(Write(o => AnnotationFlattener.Flatten(o, doc, new AnnotationFlattenOptions { RemoveAcroForm = true })));
                List<RedactionRect> rects = RedactionRects(doc, plan.Redactions);
                RedactionReport what = null!;
                doc = work.Reopen(Write(o => what = Redactor.ApplyWithReport(o, doc, new RedactionOptions { Rectangles = rects, DrawOverlay = true, OverlayColor = ColorF.Black })));
                removed = RemovedOf(what);
                nothingUnder = plan.Redactions.Select(b => b.Page).Distinct().Where(p => !removed.Any(r => r.Page == p)).Order().ToList();
                RedactionVerification verified = RedactionVerifier.Verify(doc, rects);
                if (!verified.IsClean)
                {
                    RedactionSurvivor first = verified.Survivors[0];
                    Box at = ShownBox(doc.Pages[first.PageIndex], first.Bounds.X, first.Bounds.Y, first.Bounds.Width, first.Bounds.Height);
                    return Refuse(new EditReport
                    {
                        Refused = EditRefusals.Survivor,
                        SurvivorPage = first.PageIndex,
                        SurvivorBox = new Box { X = Math.Round(at.X, 1), Y = Math.Round(at.Y, 1), W = Math.Round(at.W, 1), H = Math.Round(at.H, 1) },
                    });
                }
                doc = work.Reopen(Write(o => PdfSanitizer.Sanitize(doc, o, Disarm)));
                // Reported against the original: an earlier write may already have dropped it, and a redacted file keeps none.
                outlineRemoved = OutlineReader.GetOutlines(main).Count > 0;
                doc = work.Reopen(Write(o => OutlineWriter.Apply(o, doc, Array.Empty<OutlineEntry>())));
                // The compressor last: every other writer makes a new /Info and XMP (D-375, step 5).
                CompressionResult compressed = null!;
                final = Write(o => compressed = PdfCompressor.Compress(doc, o, new CompressionOptions
                {
                    RemoveMetadata = true,
                    RemoveDocumentInfo = true,
                    RemovePieceInfo = true,
                    RemoveAttachments = true,
                    RemoveStructTree = false,
                    AllowSignedRewrite = plan.AcceptSignedRewrite,
                    AllowEncryptedRewrite = plan.AcceptNoPassword,
                }));
                if (compressed.Skipped)
                {
                    return Refuse(EditRefusals.Skipped);
                }
                // §7.8, 7: the final bytes hold no outline, no document information, no XMP, no embedded files.
                if (Leftovers(work.Reopen(final)))
                {
                    return Refuse(EditRefusals.Leftovers);
                }
            }
            else
            {
                // §7.8, 3: every PDF Anjal saves is sanitized, last (D-368).
                final = Write(o => PdfSanitizer.Sanitize(doc, o, Disarm));
            }

            // §7.8, 4: the result opens again with the untrusted limits and its first page draws.
            int pages;
            try
            {
                PdfDocument again = work.Reopen(final);
                pages = again.PageCount;
                if (pages == 0)
                {
                    return Refuse(EditRefusals.Reopen);
                }
                _ = new PageRasterizer(again.Objects, new RenderOptions { Dpi = 36, ApplyPageRotation = true }).RasterizeToPng(again.Pages[0], ct);
            }
            catch (PdfException)
            {
                return Refuse(EditRefusals.Reopen);
            }
            return JobAnswer.File(final, ViewJson.Write(new EditReport
            {
                Pages = pages,
                Redacted = plan.Redactions.Count,
                ActiveRemoved = activeRemoved,
                FilesRemoved = filesRemoved,
                OutlineRemoved = outlineRemoved,
                Removed = removed,
                NothingUnder = nothingUnder,
            }));
        });
    }

    /// <summary>
    /// The transform from the page as shown - its CropBox turned by its <c>/Rotate</c>, origin bottom-left, y up, in
    /// points, as each overlay page is drawn - to the page's user space, where <c>PlaceMany</c> and the Redactor
    /// work: chuvadi-pdf 3.34.0's own (<c>GetDisplayTransform</c>, D-335; L-02, L-13). Anjal's own counter-rotation
    /// of rc.29's design (DES-22 §7.4) is not built.
    /// </summary>
    /// <param name="page">The page.</param>
    /// <returns>The transform.</returns>
    public static Transform DisplayToUser(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.GetDisplayTransform(PdfDisplayBox.CropBox);
    }

    /// <summary>The redaction boxes in each page's user space, each padded as DES-22 §7.6 asks of found words.</summary>
    /// <param name="doc">The document as it will be redacted.</param>
    /// <param name="boxes">The boxes, in the pages as shown.</param>
    /// <returns>The library's rectangles.</returns>
    public static List<RedactionRect> RedactionRects(PdfDocument doc, IReadOnlyList<RedactBox> boxes)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(boxes);
        var rects = new List<RedactionRect>(boxes.Count);
        foreach (RedactBox b in boxes)
        {
            // The editor's boxes are from the top-left of the page as shown; display space is from its bottom-left.
            PdfPage page = doc.Pages[b.Page];
            Transform toUser = DisplayToUser(page);
            double h = new PageGeometry(page).ShownHeight;
            PointF a = toUser.TransformPoint(new PointF(b.X, h - b.Y));
            PointF c = toUser.TransformPoint(new PointF(b.X + b.W, h - b.Y - b.H));
            rects.Add(new RedactionRect(b.Page, RectangleF.FromCorners(Math.Min(a.X, c.X), Math.Min(a.Y, c.Y), Math.Max(a.X, c.X), Math.Max(a.Y, c.Y))));
        }
        return rects;
    }

    // A user-space rectangle as a box from the top-left of the page as shown: the display transform turned back.
    private static Box ShownBox(PdfPage page, double x, double y, double width, double height)
    {
        Transform toShown = DisplayToUser(page).Invert();
        double h = new PageGeometry(page).ShownHeight;
        PointF a = toShown.TransformPoint(new PointF(x, y));
        PointF c = toShown.TransformPoint(new PointF(x + width, y + height));
        return new Box { X = Math.Min(a.X, c.X), Y = h - Math.Max(a.Y, c.Y), W = Math.Abs(c.X - a.X), H = Math.Abs(c.Y - a.Y) };
    }

    // The library's report as the person reads it: pages where something was removed, in page order.
    private static List<RedactedPage> RemovedOf(RedactionReport report) =>
        report.Pages
            .Select(p => new RedactedPage
            {
                Page = p.PageIndex,
                Characters = p.GlyphsRemoved,
                Pictures = p.ImagesBlanked + p.ImagesRemoved,
                Drawings = p.PathsCut + p.PathsRemoved,
                FormValues = p.FieldValuesCleared,
            })
            .Where(p => p.Characters + p.Pictures + p.Drawings + p.FormValues > 0)
            .OrderBy(p => p.Page)
            .ToList();

    // The arranged document's bytes: the original's own when the pages are unchanged, else Assemble's (each used
    // input flattened first when the result joins files, or when the file has a form - L-06: no /AcroForm is
    // written), then the turns. Null when a pick names a page or file that is not there.
    private static byte[]? Arranged(Work work, EditPlan plan)
    {
        PdfDocument main = work.Sources[0];
        if (plan.Pages.Count == 0)
        {
            return plan.Joined == 0 ? Write(o => PdfSanitizer.Sanitize(main, o, Disarm)) : null;
        }
        if (plan.Pages.Count > MaxPages)
        {
            return null;
        }
        foreach (PagePick pick in plan.Pages)
        {
            if (pick.File < 0 || pick.File >= work.Sources.Count || pick.Page < 0 || pick.Page >= work.Sources[pick.File].PageCount || pick.Turn is not (0 or 90 or 180 or 270))
            {
                return null;
            }
        }
        bool joins = plan.Pages.Any(p => p.File > 0);
        var used = plan.Pages.Select(p => p.File).Distinct().ToList();
        var sources = new Dictionary<int, PdfDocument>();
        foreach (int f in used)
        {
            PdfDocument d = work.Sources[f];
            sources[f] = joins || HasForm(d)
                ? work.Reopen(Write(o => AnnotationFlattener.Flatten(o, d, new AnnotationFlattenOptions { RemoveAcroForm = true })))
                : d;
        }
        PdfDocument doc = work.Reopen(Write(o => PageOperations.Assemble(o, plan.Pages.Select(p => new PageSelector(sources[p.File], p.Page)).ToList())));
        foreach (int turn in new[] { 90, 180, 270 })
        {
            var pages = plan.Pages.Select((p, i) => (p, i)).Where(x => x.p.Turn == turn).Select(x => x.i).ToList();
            if (pages.Count > 0)
            {
                PdfDocument before = doc;
                doc = work.Reopen(Write(o => PageOperations.RotatePages(o, before, turn, pages)));
            }
        }
        return Write(o => PdfSanitizer.Sanitize(doc, o, Disarm));
    }

    // Every mark on a page that is there, every picture one of the request's, every value within its limits.
    private static bool PlanFits(EditPlan plan, int pages, IReadOnlyList<byte[]> pictures)
    {
        if (plan.Marks.Count > MaxMarks || plan.Redactions.Count > MaxMarks)
        {
            return false;
        }
        foreach (EditMark m in plan.Marks)
        {
            if (m.Page < 0 || m.Page >= pages || !MarkKinds.All.Contains(m.Kind) || m.Points.Count > MaxPoints || m.Points.Count % 2 != 0
                || (m.Text?.Length ?? 0) > MaxText || !Finite(m.X, m.Y, m.W, m.H, m.Line, m.Size) || m.Line < 0 || m.Line > 50 || m.Size is < 4 or > 200
                || m.Points.Any(v => !double.IsFinite(v)) || m.Strokes.Any(s => s < 0 || s * 2 > m.Points.Count) || ParseColor(m.Color) is null)
            {
                return false;
            }
            if (m.Kind == MarkKinds.Picture && (m.Picture < 0 || m.Picture >= pictures.Count || FileKinds.PictureType(pictures[m.Picture]) is not ("image/png" or "image/jpeg")))
            {
                return false;
            }
        }
        foreach (RedactBox b in plan.Redactions)
        {
            if (b.Page < 0 || b.Page >= pages || !Finite(b.X, b.Y, b.W, b.H) || b.W <= 0 || b.H <= 0)
            {
                return false;
            }
        }
        if (plan.Watermark is WatermarkSpec w && (w.Text.Length is 0 or > MaxText || !Finite(w.Size, w.Angle, w.Opacity) || w.Size is < 4 or > 400 || w.Opacity is < 0.05 or > 1 || ParseColor(w.Color) is null))
        {
            return false;
        }
        return plan.Numbers is not NumberSpec n || (n.Format.Length is > 0 and <= MaxText && NumberPlaces.All.Contains(n.Where) && n.Size is >= 4 and <= 72 && n.Start is >= 0 and < 1_000_000);
    }

    // One overlay page per page that carries anything, drawn in the page as shown, and one watermark page per page;
    // placed with one PlaceMany (§7.4, §7.7).
    private static byte[] Overlay(Work work, PdfDocument doc, EditPlan plan, IReadOnlyList<byte[]> pictures)
    {
        PdfDocumentBuilder builder = PdfDocumentBuilder.Create().UseLipiFonts();
        var stamps = new List<(int Target, int Source, Transform Transform, double Opacity)>();
        int built = 0;
        int total = doc.PageCount;
        for (int i = 0; i < total; i++)
        {
            var g = new PageGeometry(doc.Pages[i]);
            double w = g.ShownWidth;
            double h = g.ShownHeight;
            Transform toUser = DisplayToUser(doc.Pages[i]);
            if (plan.Watermark is WatermarkSpec mark)
            {
                // A square as wide as the page's diagonal, the words across its middle, turned about its centre.
                double side = Math.Ceiling(Math.Sqrt((w * w) + (h * h)));
                PageBuilder p = builder.AddPage(new AuthoringPageSize(side, side));
                p.DrawText(mark.Text, (side - LipiText.Width(mark.Text, mark.Size)) / 2, (side - mark.Size) / 2, Lipi, mark.Size, ParseColor(mark.Color)!.Value);
                Transform turn = Transform.CreateTranslation(-side / 2, -side / 2)
                    .Multiply(Transform.CreateRotationDegrees(mark.Angle))
                    .Multiply(Transform.CreateTranslation(w / 2, h / 2))
                    .Multiply(toUser);
                stamps.Add((i, built++, turn, mark.Opacity));
            }
            var on = plan.Marks.Where(m => m.Page == i).ToList();
            if (on.Count == 0 && plan.Numbers is null)
            {
                continue;
            }
            PageBuilder page = builder.AddPage(new AuthoringPageSize(w, h));
            foreach (EditMark m in on)
            {
                Draw(page, m, pictures);
            }
            if (plan.Numbers is NumberSpec n)
            {
                string line = n.Format
                    .Replace("{n}", (n.Start + i).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("{N}", (n.Start + total - 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
                const double margin = 28;
                double width = LipiText.Width(line, n.Size);
                double y = n.Where.StartsWith("top", StringComparison.Ordinal) ? margin : h - margin - n.Size;
                double x = n.Where.EndsWith("left", StringComparison.Ordinal) ? margin
                    : n.Where.EndsWith("right", StringComparison.Ordinal) ? w - margin - width
                    : (w - width) / 2;
                page.DrawText(line, x, y, Lipi, n.Size, new Color(0.2, 0.2, 0.2));
            }
            stamps.Add((i, built++, toUser, 1));
        }
        PdfDocument source = work.Reopen(builder.ToByteArray());
        var requests = stamps.Select(s => new StampRequest(s.Target, source, s.Source, s.Transform) { Opacity = s.Opacity }).ToList();
        return Write(o => PageStamper.PlaceMany(o, doc, requests));
    }

    // One mark, in the page as shown (the builder's top-left origin is the editor's).
    private static void Draw(PageBuilder page, EditMark m, IReadOnlyList<byte[]> pictures)
    {
        Color color = ParseColor(m.Color)!.Value;
        switch (m.Kind)
        {
            case MarkKinds.Text:
                // Only DrawText takes "Lipi" (it shapes Indic runs): each line drawn on its own, wrapped by LiPi's widths.
                double top = m.Y;
                foreach (string line in LipiText.Wrap(m.Text ?? string.Empty, Math.Max(m.W, m.Size), m.Size))
                {
                    if (line.Length > 0)
                    {
                        page.DrawText(line, m.X, top, Lipi, m.Size, color);
                    }
                    top += m.Size * 1.25;
                }
                break;
            case MarkKinds.Rectangle:
                page.DrawRectangle(m.X, m.Y, m.W, m.H, null, color, m.Line);
                break;
            case MarkKinds.Ellipse:
                page.DrawPath(new Path().Ellipse(m.X + (m.W / 2), m.Y + (m.H / 2), m.W / 2, m.H / 2), null, color, m.Line);
                break;
            case MarkKinds.Highlight:
                page.PushOpacity(0.35);
                page.DrawRectangle(m.X, m.Y, m.W, m.H, color, null, 0);
                page.PopOpacity();
                break;
            case MarkKinds.Line when m.Points.Count >= 4:
                page.DrawLine(m.Points[0], m.Points[1], m.Points[2], m.Points[3], color, m.Line);
                break;
            case MarkKinds.Arrow when m.Points.Count >= 4:
                page.DrawArrow(m.Points[0], m.Points[1], m.Points[2], m.Points[3], color, m.Line);
                break;
            case MarkKinds.Pen or MarkKinds.Signature when m.Points.Count >= 4:
                var path = new Path();
                var starts = new HashSet<int>(m.Strokes) { 0 };
                for (int k = 0; k + 1 < m.Points.Count; k += 2)
                {
                    if (starts.Contains(k / 2))
                    {
                        path.MoveTo(m.Points[k], m.Points[k + 1]);
                    }
                    else
                    {
                        path.LineTo(m.Points[k], m.Points[k + 1]);
                    }
                }
                page.DrawPath(path, null, color, new LineStyle { Width = m.Line, Cap = LineCap.Round, Join = LineJoin.Round });
                break;
            case MarkKinds.Picture:
                page.DrawImage(pictures[m.Picture], m.X, m.Y, m.W, m.H);
                break;
        }
    }

    // §7.8, 7: anything a redacted file must not keep.
    private static bool Leftovers(PdfDocument doc) =>
        OutlineReader.GetOutlines(doc).Count > 0
        || doc.XmpMetadata is { Length: > 0 }
        || doc.Info is { Count: > 0 }
        || PdfSanitizer.Inspect(doc, Disarm).EmbeddedFilesRemoved > 0;

    private static List<SignatureLine> SignaturesOf(PdfDocument doc)
    {
        var lines = new List<SignatureLine>();
        foreach (PdfSignature s in SignatureReader.Read(doc.Catalog, doc.Objects))
        {
            bool valid;
            try
            {
                valid = PdfSignatureVerifier.Verify(s, doc).IsValid;
            }
            catch (Exception ex) when (ex is PdfException or InvalidOperationException or FormatException or ArgumentException or System.Security.Cryptography.CryptographicException)
            {
                valid = false;
            }
            lines.Add(new SignatureLine { Name = s.Name ?? string.Empty, Valid = valid });
        }
        return lines;
    }

    // D-405 and D-406 (the library's security advisory, 17 Nov 2026): until chuvadi-pdf 3.35.1, no redaction, and
    // no change to the pages of a file - the one edited or one joined to it - that has hidden layers.
    private static JobAnswer? HeldBack(Work work, EditPlan plan, EditSwitches switches)
    {
        if (plan.Redactions.Count > 0 && !switches.Redact)
        {
            return Refuse(EditRefusals.RedactOff);
        }
        if (!switches.LayerPages && plan.ChangesPages(work.Sources[0].PageCount) && work.Sources.Any(HasLayers))
        {
            return Refuse(EditRefusals.Layers);
        }
        return null;
    }

    /// <summary>True when the file declares hidden layers (optional content, ISO 32000 §8.11).</summary>
    /// <param name="doc">The file.</param>
    /// <returns>Whether it has optional content.</returns>
    internal static bool HasLayers(PdfDocument doc) =>
        doc.Catalog.ContainsKey(PdfName.Intern("OCProperties")) || OptionalContentReader.GetGroups(doc).Count > 0;

    private static bool HasForm(PdfDocument doc) => doc.PageCount > 0 && doc.Pages[0].AcroForm is PdfDictionary form && form.GetArray(PdfName.Intern("Fields")) is { Count: > 0 };

    private static Color? ParseColor(string? hex)
    {
        if (hex is not { Length: 7 } || hex[0] != '#' || !hex.AsSpan(1).ToString().All(Uri.IsHexDigit))
        {
            return null;
        }
        return Color.FromHex(hex);
    }

    private static bool Finite(params double[] values) => values.All(double.IsFinite);

    private static byte[] Write(Action<Stream> write)
    {
        using var output = new MemoryStream();
        write(output);
        return output.ToArray();
    }

    // A refusal: its reason in the log's detail, and the report (never content) for the page's words.
    private static JobAnswer Refuse(string why) => Refuse(new EditReport { Refused = why });

    private static JobAnswer Refuse(EditReport report) =>
        new(new ViewAnswer { Status = ViewStatus.Refused, Detail = "edit " + report.Refused, Json = ViewJson.Write(report) }, Array.Empty<byte[]>());

    // Opens the file with the untrusted limits; a password asked for, a wrong one, or too large becomes the answer.
    private static PdfDocument Open(byte[] bytes, string? password, out JobAnswer? refused)
    {
        refused = null;
        if (bytes.Length > PdfJobs.Limits.MaxInputBytes)
        {
            refused = JobAnswer.Status(ViewStatus.TooLarge, "input");
            return null!;
        }
        var stream = new MemoryStream(bytes, writable: false);
        if (string.IsNullOrEmpty(password) && PdfDocument.RequiresPassword(stream))
        {
            refused = JobAnswer.Status(ViewStatus.NeedsPassword, null);
            stream.Dispose();
            return null!;
        }
        stream.Position = 0;
        return PdfDocument.Open(stream, new PdfOpenOptions { Password = password, Limits = PdfJobs.Limits, RepairIfDamaged = true });
    }

    // DES-22 §7.1: the child catches every exception; the library's become §5's words, anything else "could not be
    // saved" with the step and the exception's type for the log - never the file's content.
    private static JobAnswer Guard(string step, Func<JobAnswer> job)
    {
        try
        {
            return job();
        }
        catch (PdfPasswordRequiredException ex)
        {
            return JobAnswer.Status(ex.PasswordSupplied ? ViewStatus.WrongPassword : ViewStatus.NeedsPassword, null);
        }
        catch (PdfLimitExceededException ex)
        {
            return JobAnswer.Status(ViewStatus.TooLarge, "pdf " + ex.LimitName);
        }
        catch (PdfException ex)
        {
            return JobAnswer.Status(ViewStatus.Damaged, "pdf " + ex.GetType().Name);
        }
        catch (TooLargeException)
        {
            return JobAnswer.Status(ViewStatus.TooLarge, "edit " + step);
        }
        catch (OperationCanceledException)
        {
            return JobAnswer.Status(ViewStatus.TooSlow, step);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return JobAnswer.Status(ViewStatus.Failed, "edit " + step + " " + ex.GetType().Name);
        }
    }

    // A step wrote more than a file may hold (DES-22 §5).
    private sealed class TooLargeException : Exception
    {
    }

    // The documents one job opens, disposed together; each written step is opened again from its bytes.
    private sealed class Work : IDisposable
    {
        private readonly List<PdfDocument> open = new();

        public List<PdfDocument> Sources { get; } = new();

        public bool OpenSources(IReadOnlyList<byte[]> files, string? password, EditPlan plan, out JobAnswer? refused)
        {
            refused = null;
            if (files.Count < 1 + plan.Joined || plan.Joined < 0 || plan.Joined > 16)
            {
                refused = Refuse(EditRefusals.Plan);
                return false;
            }
            for (int i = 0; i <= plan.Joined; i++)
            {
                string? pw = i == 0 ? password : (i - 1 < plan.JoinedPasswords.Count ? plan.JoinedPasswords[i - 1] : null);
                PdfDocument d = Open(files[i], pw, out refused);
                if (refused is not null)
                {
                    return false;
                }
                this.open.Add(d);
                this.Sources.Add(d);
            }
            return true;
        }

        public PdfDocument Reopen(byte[] bytes)
        {
            if (bytes.Length > PdfJobs.Limits.MaxInputBytes)
            {
                throw new TooLargeException();
            }
            PdfDocument d = PdfDocument.Open(new MemoryStream(bytes, writable: false), new PdfOpenOptions { Limits = PdfJobs.Limits });
            this.open.Add(d);
            return d;
        }

        public void Dispose()
        {
            foreach (PdfDocument d in this.open)
            {
                d.Dispose();
            }
        }
    }
}
