namespace Anjal.Viewing;

/// <summary>
/// rc.29 part 2 (DES-22 §7, D-369): every change the person made in the PDF editor, applied in one pass on Save.
/// Positions are in points in the page as shown (top-left origin, after the page's turn and crop), on the pages as
/// they will be (<see cref="Pages"/>), so what the person drew is where it lands.
/// </summary>
public sealed class EditPlan
{
    /// <summary>The pages in their new order, each with its turn; empty keeps the file's pages as they are.</summary>
    public List<PagePick> Pages { get; init; } = new();

    /// <summary>How many of the request's files after the first are the mail's other PDFs to join (D-371).</summary>
    public int Joined { get; init; }

    /// <summary>The passwords of the joined PDFs, in their order (null for one without).</summary>
    public List<string?> JoinedPasswords { get; init; } = new();

    /// <summary>Marks, signatures and pictures, drawn in list order (§7.4, §7.5).</summary>
    public List<EditMark> Marks { get; init; } = new();

    /// <summary>The boxes to redact (§7.6).</summary>
    public List<RedactBox> Redactions { get; init; } = new();

    /// <summary>A watermark on every page (§7.7), or none.</summary>
    public WatermarkSpec? Watermark { get; init; }

    /// <summary>Page numbers, a header or a footer on every page (§7.7), or none.</summary>
    public NumberSpec? Numbers { get; init; }

    /// <summary>The person ticked "I understand" for a digitally signed file (§7.8, 1).</summary>
    public bool AcceptSignedRewrite { get; init; }

    /// <summary>The person ticked that the result will have no password (D-374).</summary>
    public bool AcceptNoPassword { get; init; }

    /// <summary>
    /// Whether the plan changes the pages (D-406): joins another file, or lists pages other than the file's own, each
    /// in its place and with no turn.
    /// </summary>
    /// <param name="pageCount">The file's number of pages.</param>
    /// <returns>True when the pages are turned, moved, removed, kept or joined.</returns>
    public bool ChangesPages(int pageCount)
    {
        List<PagePick> pages = this.Pages ?? new();
        if (this.Joined > 0)
        {
            return true;
        }
        if (pages.Count == 0)
        {
            return false;
        }
        if (pages.Count != pageCount)
        {
            return true;
        }
        for (int i = 0; i < pages.Count; i++)
        {
            if (pages[i] is not { File: 0, Turn: 0 } p || p.Page != i)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether the plan changes nothing at all.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => (this.Pages?.Count ?? 0) == 0 && (this.Marks?.Count ?? 0) == 0 && (this.Redactions?.Count ?? 0) == 0 && this.Watermark is null && this.Numbers is null;

    /// <summary>
    /// The plan with every list there (empty when the JSON left it out): the JSON reader gives an init-only list it
    /// did not find as null, so a plan from the page is read through this before anything uses it.
    /// </summary>
    /// <returns>The plan, every list present.</returns>
    public EditPlan Whole() => new()
    {
        Pages = (this.Pages ?? new()).Where(p => p is not null).ToList(),
        Joined = this.Joined,
        JoinedPasswords = this.JoinedPasswords ?? new(),
        Marks = (this.Marks ?? new()).Where(m => m is not null).Select(m => new EditMark
        {
            Kind = m.Kind ?? string.Empty,
            Page = m.Page,
            X = m.X,
            Y = m.Y,
            W = m.W,
            H = m.H,
            Points = m.Points ?? new(),
            Strokes = m.Strokes ?? new(),
            Color = m.Color ?? "#000000",
            Line = m.Line,
            Text = m.Text,
            Size = m.Size,
            Picture = m.Picture,
        }).ToList(),
        Redactions = (this.Redactions ?? new()).Where(r => r is not null).ToList(),
        Watermark = this.Watermark is WatermarkSpec w ? new WatermarkSpec { Text = w.Text ?? string.Empty, Size = w.Size, Angle = w.Angle, Opacity = w.Opacity, Color = w.Color ?? "#808080" } : null,
        Numbers = this.Numbers is NumberSpec n ? new NumberSpec { Format = n.Format ?? string.Empty, Where = n.Where ?? string.Empty, Start = n.Start, Size = n.Size } : null,
        AcceptSignedRewrite = this.AcceptSignedRewrite,
        AcceptNoPassword = this.AcceptNoPassword,
    };
}

/// <summary>One page of the result: which file and page it comes from, and the turn added to it.</summary>
public sealed class PagePick
{
    /// <summary>The file: 0 is the one being edited, 1 and on the joined PDFs.</summary>
    public int File { get; init; }

    /// <summary>The page in that file (0-based).</summary>
    public int Page { get; init; }

    /// <summary>The turn added, clockwise: 0, 90, 180 or 270.</summary>
    public int Turn { get; init; }
}

/// <summary>One mark on a page of the result (§7.4, §7.5).</summary>
public sealed class EditMark
{
    /// <summary>What it is (<see cref="MarkKinds"/>).</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>The page of the result (0-based).</summary>
    public int Page { get; init; }

    /// <summary>The left of its box, in points.</summary>
    public double X { get; init; }

    /// <summary>The top of its box, in points.</summary>
    public double Y { get; init; }

    /// <summary>Its box's width, in points.</summary>
    public double W { get; init; }

    /// <summary>Its box's height, in points.</summary>
    public double H { get; init; }

    /// <summary>For a line, an arrow, the pen and a drawn signature: the points, as x and y in turn.</summary>
    public List<double> Points { get; init; } = new();

    /// <summary>For a drawn signature: where each stroke starts in <see cref="Points"/> (as a point's number).</summary>
    public List<int> Strokes { get; init; } = new();

    /// <summary>Its colour, "#rrggbb".</summary>
    public string Color { get; init; } = "#000000";

    /// <summary>The line's width, in points.</summary>
    public double Line { get; init; } = 1.5;

    /// <summary>For a text box: its words.</summary>
    public string? Text { get; init; }

    /// <summary>For a text box: the size of its letters, in points.</summary>
    public double Size { get; init; } = 12;

    /// <summary>For a picture: which of the request's pictures (0 is the first after the PDFs).</summary>
    public int Picture { get; init; } = -1;
}

/// <summary>The kinds of mark (§7.4, §7.5).</summary>
public static class MarkKinds
{
    /// <summary>A text box.</summary>
    public const string Text = "text";

    /// <summary>A rectangle.</summary>
    public const string Rectangle = "rect";

    /// <summary>An ellipse.</summary>
    public const string Ellipse = "ellipse";

    /// <summary>A straight line.</summary>
    public const string Line = "line";

    /// <summary>An arrow.</summary>
    public const string Arrow = "arrow";

    /// <summary>The freehand pen.</summary>
    public const string Pen = "pen";

    /// <summary>A highlight: a filled box at 35% opacity.</summary>
    public const string Highlight = "highlight";

    /// <summary>A picture the person added (JPEG or PNG), or their own scanned signature.</summary>
    public const string Picture = "picture";

    /// <summary>A signature drawn on the pad, kept as strokes.</summary>
    public const string Signature = "sign";

    /// <summary>Every kind.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Text, Rectangle, Ellipse, Line, Arrow, Pen, Highlight, Picture, Signature };
}

/// <summary>A box to redact, on a page of the result.</summary>
public sealed class RedactBox
{
    /// <summary>The page of the result (0-based).</summary>
    public int Page { get; init; }

    /// <summary>From the left, in points.</summary>
    public double X { get; init; }

    /// <summary>From the top, in points.</summary>
    public double Y { get; init; }

    /// <summary>Its width, in points.</summary>
    public double W { get; init; }

    /// <summary>Its height, in points.</summary>
    public double H { get; init; }
}

/// <summary>A watermark drawn across every page (§7.7, D-376).</summary>
public sealed class WatermarkSpec
{
    /// <summary>Its words.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The size of its letters, in points.</summary>
    public double Size { get; init; } = 60;

    /// <summary>Its angle, anticlockwise, in degrees.</summary>
    public double Angle { get; init; } = 45;

    /// <summary>How strongly it shows, from 0.05 to 1.</summary>
    public double Opacity { get; init; } = 0.2;

    /// <summary>Its colour, "#rrggbb".</summary>
    public string Color { get; init; } = "#808080";
}

/// <summary>Page numbers, or a header or footer line, on every page (§7.7, D-376).</summary>
public sealed class NumberSpec
{
    /// <summary>The words, with {n} for the page's number and {N} for how many there are.</summary>
    public string Format { get; init; } = "Page {n} of {N}";

    /// <summary>Where (<see cref="NumberPlaces"/>).</summary>
    public string Where { get; init; } = NumberPlaces.BottomCentre;

    /// <summary>The number of the first page.</summary>
    public int Start { get; init; } = 1;

    /// <summary>The size of its letters, in points.</summary>
    public double Size { get; init; } = 10;
}

/// <summary>Where page numbers go.</summary>
public static class NumberPlaces
{
    /// <summary>At the top, on the left.</summary>
    public const string TopLeft = "top-left";

    /// <summary>At the top, in the middle.</summary>
    public const string TopCentre = "top-centre";

    /// <summary>At the top, on the right.</summary>
    public const string TopRight = "top-right";

    /// <summary>At the bottom, on the left.</summary>
    public const string BottomLeft = "bottom-left";

    /// <summary>At the bottom, in the middle.</summary>
    public const string BottomCentre = "bottom-centre";

    /// <summary>At the bottom, on the right.</summary>
    public const string BottomRight = "bottom-right";

    /// <summary>Every place.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { TopLeft, TopCentre, TopRight, BottomLeft, BottomCentre, BottomRight };
}

/// <summary>What the editor must know before it opens (§7.8, 1; D-374).</summary>
public sealed class EditCheck
{
    /// <summary>The file's digital signatures, each with who signed it and whether it holds.</summary>
    public List<SignatureLine> Signatures { get; init; } = new();

    /// <summary>The file has a password (the result will not, D-374).</summary>
    public bool Locked { get; init; }

    /// <summary>The file has form fields (they become part of the page when its pages are rearranged or joined).</summary>
    public bool HasForm { get; init; }

    /// <summary>The file holds scripts, actions or embedded files (the result never does, D-368).</summary>
    public bool Active { get; init; }

    /// <summary>The number of pages.</summary>
    public int PageCount { get; init; }

    /// <summary>The file has hidden layers (optional content): its pages are not changed until chuvadi-pdf 3.35.1 (D-406).</summary>
    public bool HasLayers { get; init; }
}

/// <summary>One digital signature in a file.</summary>
public sealed class SignatureLine
{
    /// <summary>Who signed it, as the signature names them.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Whether it holds (the file not changed since, and the signature itself sound).</summary>
    public bool Valid { get; init; }
}

/// <summary>What Save did (§7.6; logged as counts, never content).</summary>
public sealed class EditReport
{
    /// <summary>The pages of the result.</summary>
    public int Pages { get; init; }

    /// <summary>The boxes redacted.</summary>
    public int Redacted { get; init; }

    /// <summary>Scripts, actions and the like the sanitizer removed.</summary>
    public int ActiveRemoved { get; init; }

    /// <summary>Embedded files the sanitizer removed.</summary>
    public int FilesRemoved { get; init; }

    /// <summary>The outline was removed (a redacted file keeps none, D-375).</summary>
    public bool OutlineRemoved { get; init; }

    /// <summary>When the save is refused: the page (0-based) where something still shows inside a box.</summary>
    public int? SurvivorPage { get; init; }

    /// <summary>When the save is refused: the box something still shows in.</summary>
    public Box? SurvivorBox { get; init; }

    /// <summary>When the save is refused: why (<see cref="EditRefusals"/>).</summary>
    public string? Refused { get; init; }

    /// <summary>
    /// What the redaction removed, page by page (chuvadi-pdf 3.34.0 <c>Redactor.ApplyWithReport</c>), shown to the
    /// person before the file is attached (decisions of 15 Nov 2026). Pages where nothing was removed are not listed.
    /// </summary>
    public List<RedactedPage> Removed { get; init; } = new();

    /// <summary>The pages (0-based) that have a redaction box under which nothing was found: a note, not a refusal.</summary>
    public List<int> NothingUnder { get; init; } = new();
}

/// <summary>What the redaction removed on one page.</summary>
public sealed class RedactedPage
{
    /// <summary>The page, 0-based, in the file as saved.</summary>
    public int Page { get; init; }

    /// <summary>Characters of text removed.</summary>
    public int Characters { get; init; }

    /// <summary>Pictures removed whole or in part.</summary>
    public int Pictures { get; init; }

    /// <summary>Drawn lines and shapes removed whole or in part.</summary>
    public int Drawings { get; init; }

    /// <summary>Values of form fields cleared.</summary>
    public int FormValues { get; init; }
}

/// <summary>Why a save is refused (DES-22 §7.6, §7.8).</summary>
public static class EditRefusals
{
    /// <summary>Something still shows inside a redaction box (§7.6, the gate).</summary>
    public const string Survivor = "survivor";

    /// <summary>The file is digitally signed and the person did not accept that the signature will no longer hold.</summary>
    public const string DigitallySigned = "signed";

    /// <summary>The file has a password and the person did not accept that the result will not.</summary>
    public const string Password = "password";

    /// <summary>The compressor skipped the file, so its metadata could not be removed (D-375, step 6).</summary>
    public const string Skipped = "skipped";

    /// <summary>The final bytes still hold an outline, document information, XMP or embedded files (§7.8, 7).</summary>
    public const string Leftovers = "leftovers";

    /// <summary>The result did not open again, or its first page could not be drawn (§7.8, 4).</summary>
    public const string Reopen = "reopen";

    /// <summary>The plan does not fit the file (a page or a picture that is not there).</summary>
    public const string Plan = "plan";

    /// <summary>The plan redacts, and redaction is off until chuvadi-pdf 3.35.1 (D-405).</summary>
    public const string RedactOff = "redact-off";

    /// <summary>The plan changes the pages of a file with hidden layers, which waits for chuvadi-pdf 3.35.1 (D-406).</summary>
    public const string Layers = "layers";
}
