using Anjal.Viewing;
using Chuvadi.Pdf.Documents;
using Chuvadi.Pdf.Forms;
using Chuvadi.Pdf.Operations;
using Chuvadi.Pdf.Primitives;
using Chuvadi.Pdf.Rendering;
using Chuvadi.Pdf.Rendering.DisplayList;
using Chuvadi.Pdf.Text;
using TextRun = Anjal.Viewing.TextRun;

namespace Anjal.Viewer;

/// <summary>
/// rc.29 (DES-22 §3.1, D-359): a PDF drawn by Anjal's viewer instead of the browser's own - its pages as pictures,
/// its text laid over them, Find, the outline, a password asked for, and the notices. Every call opens the file
/// with the untrusted limits of DES-22 §5 and a cancellation token.
/// </summary>
public static class PdfJobs
{
    /// <summary>The widest page picture drawn (DES-22 §3.1).</summary>
    public const int MaxWidth = 2000;

    /// <summary>The narrowest page picture drawn (a thumbnail is 120 px).</summary>
    public const int MinWidth = 60;

    /// <summary>At most this many matches are given back from one search.</summary>
    public const int MaxHits = 500;

    /// <summary>DES-22 §5: the limits every PDF is opened with.</summary>
    public static PdfLimits Limits { get; } = new()
    {
        MaxInputBytes = 64L * 1024 * 1024,
        MaxPageCount = 2000,
        MaxMemoryBytes = 512L * 1024 * 1024,
        MaxDecodedStreamBytes = 128L * 1024 * 1024,
        MaxContentOperators = 2_000_000,
        MaxOutputPixels = 16_000_000,
    };

    /// <summary>A PDF's pages as shown, its outline and its notices.</summary>
    /// <param name="bytes">The file.</param>
    /// <param name="password">Its password, or null.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The answer.</returns>
    public static JobAnswer Info(byte[] bytes, string? password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var warnings = new List<PdfWarning>();
        return WithDocument(bytes, password, warnings, doc =>
        {
            var info = new PdfInfo { PageCount = doc.PageCount };
            int shown = Math.Min(doc.PageCount, Limits.MaxPageCount ?? 2000);
            for (int i = 0; i < shown; i++)
            {
                ct.ThrowIfCancellationRequested();
                var g = new PageGeometry(doc.Pages[i]);
                info.Pages.Add(new PageSize { W = Math.Round(g.ShownWidth, 2), H = Math.Round(g.ShownHeight, 2) });
            }
            foreach (OutlineItem item in OutlineReader.GetOutlines(doc))
            {
                Flatten(item, 0, info.Outline);
            }
            if (doc.PageCount > shown)
            {
                info.Notices.Add(ViewNotices.ManyPages);
            }
            if (doc.IsXfa)
            {
                info.Notices.Add(ViewNotices.Xfa);
            }
            if (doc.IsRecovered)
            {
                info.Notices.Add(ViewNotices.Incomplete);
            }
            if (ActiveContent(doc))
            {
                info.Notices.Add(ViewNotices.PdfActive);
            }
            return JobAnswer.Json(ViewJson.Write(info));
        });
    }

    /// <summary>One page drawn as a PNG at a width in pixels (60 to 2,000).</summary>
    /// <param name="bytes">The file.</param>
    /// <param name="password">Its password, or null.</param>
    /// <param name="page">The page (0-based).</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The answer, with the picture as its one file.</returns>
    public static JobAnswer Page(byte[] bytes, string? password, int page, int width, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var warnings = new List<PdfWarning>();
        return WithDocument(bytes, password, warnings, doc =>
        {
            if (page < 0 || page >= doc.PageCount)
            {
                return JobAnswer.Status(ViewStatus.Failed, "page out of range");
            }
            PdfPage p = doc.Pages[page];
            var g = new PageGeometry(p);
            int px = Math.Clamp(width, MinWidth, MaxWidth);
            double dpi = g.ShownWidth > 0 ? px * 72.0 / g.ShownWidth : 72;
            var rasterizer = new PageRasterizer(doc.Objects, new RenderOptions { Dpi = dpi, ApplyPageRotation = true });
            byte[] png = rasterizer.RasterizeToPng(p, ct);
            string? notice = warnings.Any(w => w.Kind != PdfWarningKind.FontSubstituted) ? ViewNotices.Partial : null;
            return JobAnswer.File(png, notice is null ? null : "{\"notice\":\"" + notice + "\"}");
        });
    }

    /// <summary>One page's text runs, placed in the page as shown.</summary>
    /// <param name="bytes">The file.</param>
    /// <param name="password">Its password, or null.</param>
    /// <param name="page">The page (0-based).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The answer.</returns>
    public static JobAnswer Text(byte[] bytes, string? password, int page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return WithDocument(bytes, password, null, doc =>
        {
            if (page < 0 || page >= doc.PageCount)
            {
                return JobAnswer.Status(ViewStatus.Failed, "page out of range");
            }
            PdfPage p = doc.Pages[page];
            var g = new PageGeometry(p);
            var text = new PageText();
            var extractor = new TextExtractor(doc.Objects);
            foreach (Chuvadi.Pdf.Content.TextFragment f in extractor.ExtractFragments(p, ct))
            {
                if (string.IsNullOrWhiteSpace(f.Text) || double.IsNaN(f.X) || double.IsNaN(f.Y))
                {
                    continue;
                }
                (double x, double y) = g.ToShown(f.X, f.Y);
                text.Runs.Add(new TextRun
                {
                    T = f.Text,
                    X = Math.Round(x, 2),
                    Y = Math.Round(y, 2),
                    S = Math.Round(double.IsNaN(f.FontSize) || f.FontSize <= 0 ? 10 : f.FontSize, 2),
                    A = Math.Round(g.ToShownAngle(double.IsNaN(f.Angle) ? 0 : f.Angle), 2),
                    W = double.IsNaN(f.Width) || f.Width <= 0 ? 0 : Math.Round(f.Width, 2),
                });
            }
            return JobAnswer.Json(ViewJson.Write(text));
        });
    }

    /// <summary>Finds words; each match's boxes are placed in its page as shown.</summary>
    /// <param name="bytes">The file.</param>
    /// <param name="password">Its password, or null.</param>
    /// <param name="query">The words.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The answer.</returns>
    public static JobAnswer Find(byte[] bytes, string? password, string query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return WithDocument(bytes, password, null, doc =>
        {
            var result = new FindResult();
            var hits = new List<FindHit>();
            bool more = false;
            using (PdfWorkScope.Enter(Limits, ct))
            {
                IAsyncEnumerator<SearchMatch> e = DocumentSearch.SearchAsync(doc, query, new SearchOptions(), ct).GetAsyncEnumerator(ct);
                try
                {
                    while (e.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                    {
                        if (hits.Count >= MaxHits)
                        {
                            more = true;
                            break;
                        }
                        SearchMatch m = e.Current;
                        // PageNumber is zero-based despite its name (L-03).
                        if (m.PageNumber < 0 || m.PageNumber >= doc.PageCount)
                        {
                            continue;
                        }
                        var g = new PageGeometry(doc.Pages[m.PageNumber]);
                        var hit = new FindHit { Page = m.PageNumber };
                        foreach (Rect r in m.BoundingBoxes)
                        {
                            Box b = g.ToShownBox(r.X, r.Y, r.Width, r.Height);
                            hit.Boxes.Add(new Box { X = Math.Round(b.X, 2), Y = Math.Round(b.Y, 2), W = Math.Round(b.W, 2), H = Math.Round(b.H, 2) });
                        }
                        hits.Add(hit);
                    }
                }
                finally
                {
                    e.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            return JobAnswer.Json(ViewJson.Write(new FindResult { Hits = hits, More = more }));
        });
    }

    /// <summary>A copy without scripts, actions, embedded files, XFA and multimedia (D-367: Download without them).</summary>
    /// <param name="bytes">The file.</param>
    /// <param name="password">Its password, or null.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The answer, with the copy as its one file.</returns>
    public static JobAnswer Clean(byte[] bytes, string? password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return WithDocument(bytes, password, null, doc =>
        {
            using var output = new MemoryStream();
            using (PdfWorkScope.Enter(Limits, ct))
            {
                PdfSanitizer.Sanitize(doc, output, new SanitizeOptions { RemoveEmbeddedFiles = true, RemoveXfa = true, RemoveMultimedia = true, KeepUriLinks = true, AllowEncryptedInput = true });
            }
            return JobAnswer.File(output.ToArray(), null);
        });
    }

    /// <summary>Whether the PDF holds scripts, actions or embedded files (DES-22 §8).</summary>
    /// <param name="doc">The document.</param>
    /// <returns>True when it does.</returns>
    public static bool ActiveContent(PdfDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        SanitizeReport report = PdfSanitizer.Inspect(doc, new SanitizeOptions { RemoveEmbeddedFiles = true, RemoveXfa = true, RemoveMultimedia = true, KeepUriLinks = true, AllowEncryptedInput = true });
        return report.FoundActiveContent;
    }

    private static void Flatten(OutlineItem item, int depth, List<OutlineLine> lines)
    {
        if (lines.Count >= 2000 || depth > 16)
        {
            return;
        }
        lines.Add(new OutlineLine { Title = item.Title ?? string.Empty, Page = item.DestinationPageIndex, Depth = depth });
        foreach (OutlineItem child in item.Children)
        {
            Flatten(child, depth + 1, lines);
        }
    }

    // Opens the PDF with the untrusted limits (a damaged file repaired in memory), runs the job, and turns the
    // library's exceptions into the statuses of DES-22 §5. Any other exception is the caller's to catch.
    private static JobAnswer WithDocument(byte[] bytes, string? password, List<PdfWarning>? warnings, Func<PdfDocument, JobAnswer> job)
    {
        if (bytes.Length > Limits.MaxInputBytes)
        {
            return JobAnswer.Status(ViewStatus.TooLarge, "input");
        }
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            // D-405 (the library's advisory, 17 Nov 2026): the limits hold from the first byte read, the password
            // probe included (the open takes them in its options; a job that opened its own scope keeps it).
            bool needsPassword;
            using (PdfWorkScope.Current is null ? PdfWorkScope.Enter(Limits, CancellationToken.None) : null)
            {
                needsPassword = string.IsNullOrEmpty(password) && PdfDocument.RequiresPassword(stream);
            }
            if (needsPassword)
            {
                return JobAnswer.Status(ViewStatus.NeedsPassword, null);
            }
            stream.Position = 0;
            using PdfDocument doc = PdfDocument.Open(stream, new PdfOpenOptions
            {
                Password = password,
                Limits = Limits,
                LeaveOpen = true,
                RepairIfDamaged = true,
                OnWarning = warnings is null ? null : warnings.Add,
            });
            return job(doc);
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
    }
}
