namespace Anjal.Viewing;

/// <summary>rc.29 (DES-22 §3.1): a PDF as the viewer found it - its pages' sizes as shown, its outline and notices.</summary>
public sealed class PdfInfo
{
    /// <summary>The pages, each with its size as shown (after its turn and crop), in points.</summary>
    public List<PageSize> Pages { get; init; } = new();

    /// <summary>The pages the file has; more than <see cref="Pages"/> when it has more than are shown (2,000).</summary>
    public int PageCount { get; init; }

    /// <summary>The outline (bookmarks), flattened with their depth.</summary>
    public List<OutlineLine> Outline { get; init; } = new();

    /// <summary>Notices to show above the pages (<see cref="ViewNotices"/>).</summary>
    public List<string> Notices { get; init; } = new();
}

/// <summary>A page's size as shown, in points.</summary>
public sealed class PageSize
{
    /// <summary>The width as shown.</summary>
    public double W { get; init; }

    /// <summary>The height as shown.</summary>
    public double H { get; init; }
}

/// <summary>One outline entry.</summary>
public sealed class OutlineLine
{
    /// <summary>Its title, as the file has it (escaped when shown).</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>The page it goes to (0-based), or -1.</summary>
    public int Page { get; init; }

    /// <summary>How deep it sits (0 at the top).</summary>
    public int Depth { get; init; }
}

/// <summary>rc.29 (DES-22 §3.1): a page's text and where each run sits, in the page as shown.</summary>
public sealed class PageText
{
    /// <summary>The runs, in the order the file gives them.</summary>
    public List<TextRun> Runs { get; init; } = new();
}

/// <summary>
/// One run of text on a shown page: its baseline's start (from the top-left, in points), its size, its turn
/// (degrees clockwise on the screen) and its width (0 when the file does not say).
/// </summary>
public sealed class TextRun
{
    /// <summary>The text.</summary>
    public string T { get; init; } = string.Empty;

    /// <summary>The baseline's start, from the left.</summary>
    public double X { get; init; }

    /// <summary>The baseline's start, from the top.</summary>
    public double Y { get; init; }

    /// <summary>The font size.</summary>
    public double S { get; init; }

    /// <summary>The turn, degrees clockwise.</summary>
    public double A { get; init; }

    /// <summary>The width along the baseline; 0 when unknown.</summary>
    public double W { get; init; }
}

/// <summary>rc.29 (DES-22 §3.1): what Find found.</summary>
public sealed class FindResult
{
    /// <summary>The matches, page by page.</summary>
    public List<FindHit> Hits { get; init; } = new();

    /// <summary>True when there were more than were given.</summary>
    public bool More { get; init; }
}

/// <summary>One match: its page and the boxes it covers, in the page as shown.</summary>
public sealed class FindHit
{
    /// <summary>The page (0-based).</summary>
    public int Page { get; init; }

    /// <summary>rc.29 part 2: for a patient sign found by <see cref="ViewKinds.PdfSigns"/>, which kind (record, aadhaar, pan, phone).</summary>
    public string? Sign { get; init; }

    /// <summary>The boxes.</summary>
    public List<Box> Boxes { get; init; } = new();
}

/// <summary>A box on a shown page, from its top-left, in points.</summary>
public sealed class Box
{
    /// <summary>From the left.</summary>
    public double X { get; init; }

    /// <summary>From the top.</summary>
    public double Y { get; init; }

    /// <summary>Its width.</summary>
    public double W { get; init; }

    /// <summary>Its height.</summary>
    public double H { get; init; }
}

/// <summary>
/// rc.29 (DES-22 §3.2, AC-06, AC-24): a Word document as text - blocks in order, with each run's bold, italic,
/// underline, strikethrough, raised or lowered text and capitals - pictures beside the answer.
/// </summary>
public sealed class WordView
{
    /// <summary>The blocks, in the document's order.</summary>
    public List<WordBlock> Blocks { get; init; } = new();

    /// <summary>Notices to show above the text.</summary>
    public List<string> Notices { get; init; } = new();

    /// <summary>Parts of the file the reader could not use, by name.</summary>
    public List<string> Skipped { get; init; } = new();
}

/// <summary>
/// One block of a Word document: a heading (K "h1"-"h6"), a paragraph ("p"), a list item ("li", with its level),
/// a table ("table", with its rows) or a picture ("img", the picture's place among the answer's files, or -1 for a
/// picture of a kind Anjal does not show).
/// </summary>
public sealed class WordBlock
{
    /// <summary>The kind.</summary>
    public string K { get; init; } = "p";

    /// <summary>A list item's level (1 at the top).</summary>
    public int L { get; init; }

    /// <summary>The text.</summary>
    public string? T { get; init; }

    /// <summary>
    /// AC-24: the text as runs with their formatting; null when the whole text is plain, and then <see cref="T"/> is
    /// shown.
    /// </summary>
    public List<WordRun>? R { get; init; }

    /// <summary>A table's rows.</summary>
    public List<List<string>>? Rows { get; init; }

    /// <summary>AC-24: a table's cells as runs, row by row; a cell is null when its text is plain.</summary>
    public List<List<List<WordRun>?>>? RunRows { get; init; }

    /// <summary>A picture's place among the answer's files; -1 when it is not shown.</summary>
    public int Img { get; init; } = -1;

    /// <summary>A picture's own description, from the file.</summary>
    public string? Alt { get; init; }
}

/// <summary>
/// AC-24: one stretch of a Word paragraph with one formatting, as the library resolves it (document defaults, styles,
/// then the run's own). A line break is a run whose text is a single new line.
/// </summary>
public sealed class WordRun
{
    /// <summary>The text.</summary>
    public string T { get; init; } = string.Empty;

    /// <summary>Bold.</summary>
    public bool B { get; init; }

    /// <summary>Italic.</summary>
    public bool I { get; init; }

    /// <summary>Underlined (any underline style).</summary>
    public bool U { get; init; }

    /// <summary>Struck through (single or double).</summary>
    public bool S { get; init; }

    /// <summary>"sup" raised, "sub" lowered, or null.</summary>
    public string? P { get; init; }

    /// <summary>"caps" all capitals, "small" small capitals, or null.</summary>
    public string? C { get; init; }

    /// <summary>True when the run has no formatting to show.</summary>
    /// <returns>Whether it is plain.</returns>
    public bool IsPlain() => !this.B && !this.I && !this.U && !this.S && this.P is null && this.C is null;

    /// <summary>True when the other run has the same formatting.</summary>
    /// <param name="other">The other run.</param>
    /// <returns>Whether the two can be one.</returns>
    public bool SameLook(WordRun other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return this.B == other.B && this.I == other.I && this.U == other.U && this.S == other.S && this.P == other.P && this.C == other.C;
    }
}

/// <summary>rc.29 (DES-22 §3.3, D-361): one sheet of a workbook, 2,000 rows at most, the real size stated.</summary>
public sealed class SheetView
{
    /// <summary>All the workbook's sheets, for the tabs.</summary>
    public List<SheetTab> Sheets { get; init; } = new();

    /// <summary>The sheet shown (0-based).</summary>
    public int Index { get; init; }

    /// <summary>The rows shown that hold anything.</summary>
    public List<SheetRow> Rows { get; init; } = new();

    /// <summary>Column widths in characters, from the first column; 0 for the sheet's default.</summary>
    public List<double> Widths { get; init; } = new();

    /// <summary>Hidden columns (1-based).</summary>
    public List<int> HiddenColumns { get; init; } = new();

    /// <summary>Merged ranges ("A4:B4").</summary>
    public List<string> Merges { get; init; } = new();

    /// <summary>How many columns the grid has.</summary>
    public int Columns { get; init; }

    /// <summary>The last row shown.</summary>
    public int Shown { get; init; }

    /// <summary>The sheet's last row with anything in it, as the file says (0 when the file does not say).</summary>
    public int Total { get; init; }

    /// <summary>True when the sheet has more rows than are drawn (2,000).</summary>
    public bool Cut { get; init; }

    /// <summary>Notices to show above the grid.</summary>
    public List<string> Notices { get; init; } = new();
}

/// <summary>One sheet's tab.</summary>
public sealed class SheetTab
{
    /// <summary>Its name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>True for a hidden sheet (shown dimmed).</summary>
    public bool Hidden { get; init; }
}

/// <summary>One row of a sheet.</summary>
public sealed class SheetRow
{
    /// <summary>Its number (1-based).</summary>
    public int N { get; init; }

    /// <summary>Its cells that hold anything.</summary>
    public List<SheetCell> Cells { get; init; } = new();
}

/// <summary>One cell: its column (1-based), what it shows (the file's own format), and its stored value.</summary>
public sealed class SheetCell
{
    /// <summary>The column.</summary>
    public int C { get; init; }

    /// <summary>What it shows.</summary>
    public string D { get; init; } = string.Empty;

    /// <summary>Its stored value, when that differs from what it shows.</summary>
    public string? V { get; init; }
}

/// <summary>rc.29 (AC-17): what is inside an archive, read in the viewer process without unpacking it.</summary>
public sealed class ArchiveView
{
    /// <summary>The entries.</summary>
    public List<ArchiveItem> Entries { get; init; } = new();

    /// <summary>Why it is not listed (<see cref="ArchiveRefusals"/>); null when listed.</summary>
    public string? Refused { get; init; }

    /// <summary>
    /// AC-20: true when the archive is locked with a password and was opened with it - its files are shown here
    /// only, were not virus-scanned, and are never given out one by one (only the original archive downloads).
    /// </summary>
    public bool Locked { get; init; }

    /// <summary>Its size unpacked.</summary>
    public long Expanded { get; init; }
}

/// <summary>One entry of an archive.</summary>
public sealed class ArchiveItem
{
    /// <summary>Its place in the archive; negative for a folder that has no entry of its own, only files under it.</summary>
    public int Index { get; init; }

    /// <summary>Its full path inside.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Its own name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>How many folders deep.</summary>
    public int Depth { get; init; }

    /// <summary>True for a folder.</summary>
    public bool Folder { get; init; }

    /// <summary>Its size unpacked.</summary>
    public long Size { get; init; }

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset Modified { get; init; }

    /// <summary>True for a program: never offered, never opened.</summary>
    public bool Blocked { get; init; }

    /// <summary>AC-20: true when the entry uses a method Anjal cannot unpack (BZip2, LZMA, Deflate64, PPMd, strong encryption).</summary>
    public bool Unsupported { get; init; }

    /// <summary>
    /// The viewer that opens it (<see cref="FileKinds"/>): "pdf", "word", "sheet", "picture", "archive",
    /// "dicom"; null when it is only a Download.
    /// </summary>
    public string? Opens { get; init; }
}

/// <summary>
/// rc.29 (DES-22 §3.4; rc.30 on SIAARA v0.1.0-rc.1): a DICOM file's preview - its outcome, and the caption fields as
/// text from the file (escaped when shown). The patient's name and ID are never here. The picture travels as the
/// answer's first file, and for several frames the strip (the frames side by side, one picture) as the second.
/// </summary>
public sealed class DicomView
{
    /// <summary>Ok, NotDicom, NoPixelData, Unsupported, TooLarge or Damaged.</summary>
    public string Outcome { get; init; } = string.Empty;

    /// <summary>The modality.</summary>
    public string? Modality { get; init; }

    /// <summary>The study date.</summary>
    public string? StudyDate { get; init; }

    /// <summary>The series description.</summary>
    public string? Series { get; init; }

    /// <summary>The rows.</summary>
    public string? Rows { get; init; }

    /// <summary>The columns.</summary>
    public string? Columns { get; init; }

    /// <summary>The frames.</summary>
    public string? Frames { get; init; }

    /// <summary>The transfer syntax's name.</summary>
    public string? TransferSyntax { get; init; }

    /// <summary>True when the file was cut off or damaged: always shown.</summary>
    public bool Incomplete { get; init; }

    /// <summary>Why there is no picture, from the toolkit.</summary>
    public string? Reason { get; init; }

    /// <summary>rc.30: the strip of several frames is the answer's second file; how many frames it shows.</summary>
    public int StripFrames { get; init; }
}

/// <summary>
/// rc.30 (D-366): a DICOM file's Patient's Name and Patient ID, read only when the mailbox's own person asks (Show
/// patient details), and logged.
/// </summary>
public sealed class DicomPatient
{
    /// <summary>Patient's Name, as stored.</summary>
    public string? Name { get; init; }

    /// <summary>Patient ID, as stored.</summary>
    public string? Id { get; init; }
}

/// <summary>
/// rc.30 (AC-19, D-386; DES-22 §18.3): the DICOM files of a ZIP grouped by patient, study and series with SIAARA's
/// grouping (and the ZIP's DICOMDIR, when it has one). No patient's name or ID: patients are told apart by number.
/// </summary>
public sealed class DicomStudyView
{
    /// <summary>The patients, in the toolkit's order.</summary>
    public List<DicomPatientGroup> Patients { get; init; } = new();

    /// <summary>DICOM files the ZIP holds (by their mark).</summary>
    public int Files { get; init; }

    /// <summary>Files the grouping read.</summary>
    public int FilesRead { get; init; }

    /// <summary>Files that could not be put in a series.</summary>
    public int Ungrouped { get; init; }

    /// <summary>Files whose header could not be read whole (ReadLoss above 0).</summary>
    public int Incomplete { get; init; }

    /// <summary>The grouping's limit that was reached (the toolkit's name for it), or null when none was.</summary>
    public string? Budget { get; init; }

    /// <summary>Files of the ZIP not read for the grouping: too large, refused or unreadable.</summary>
    public int Skipped { get; init; }

    /// <summary>What the grouping found worth saying, counted by kind (the toolkit's names).</summary>
    public List<DicomIssueCount> Issues { get; init; } = new();
}

/// <summary>rc.30: one patient of a grouped study (by number; no name or ID).</summary>
public sealed class DicomPatientGroup
{
    /// <summary>The patient's number, from 1.</summary>
    public int Number { get; init; }

    /// <summary>The patient's studies.</summary>
    public List<DicomStudyGroup> Studies { get; init; } = new();
}

/// <summary>rc.30: one study.</summary>
public sealed class DicomStudyGroup
{
    /// <summary>The study date, as stored (display-safe).</summary>
    public string? Date { get; init; }

    /// <summary>The study's series.</summary>
    public List<DicomSeriesGroup> Series { get; init; } = new();
}

/// <summary>rc.30: one series and its slices, in the toolkit's order.</summary>
public sealed class DicomSeriesGroup
{
    /// <summary>The series' number in the whole grouping, from 1 (the address of its series view).</summary>
    public int Number { get; init; }

    /// <summary>The modality (display-safe).</summary>
    public string? Modality { get; init; }

    /// <summary>The series description (display-safe).</summary>
    public string? Description { get; init; }

    /// <summary>The files of the series.</summary>
    public int Slices { get; init; }

    /// <summary>The frames in all: a multi-frame file counts its frames.</summary>
    public long Frames { get; init; }

    /// <summary>The ZIP entry of each slice, in order (the archive's places, as the archive viewer names them).</summary>
    public List<string> Entries { get; init; } = new();
}

/// <summary>rc.30: how often the grouping found one kind of thing to say.</summary>
public sealed class DicomIssueCount
{
    /// <summary>The kind, the toolkit's name.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>How many.</summary>
    public int Count { get; init; }
}

/// <summary>A file given back whole (a Download from an archive, or a picture): its name and type.</summary>
public sealed class FileOut
{
    /// <summary>Its name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Its type, judged by its bytes for a picture.</summary>
    public string Type { get; init; } = "application/octet-stream";
}

/// <summary>rc.29 (AC-17): why an archive is not listed.</summary>
public static class ArchiveRefusals
{
    /// <summary>More entries than are listed (2,000).</summary>
    public const string TooMany = "too-many";

    /// <summary>Larger unpacked than allowed (200 MB).</summary>
    public const string TooLarge = "too-large";

    /// <summary>Packed more tightly than an ordinary archive (100:1).</summary>
    public const string TooPacked = "too-packed";

    /// <summary>Not a readable ZIP.</summary>
    public const string Unreadable = "unreadable";

    /// <summary>An archive inside an archive deeper than two levels.</summary>
    public const string TooDeep = "too-deep";

    /// <summary>AC-20: an archive made in a way Anjal cannot open (a split archive, or a method it does not unpack).</summary>
    public const string Unsupported = "unsupported";
}

/// <summary>rc.29 (DES-22 §2): the notices shown above a viewed file, as codes the webmail turns into words.</summary>
public static class ViewNotices
{
    /// <summary>Not every part could be shown (a picture the library cannot draw yet, a damaged page, a skipped part).</summary>
    public const string Partial = "partial";

    /// <summary>A Word or Excel file with macros.</summary>
    public const string Macros = "macros";

    /// <summary>A Word file with other active content (ActiveX, embedded objects, links to outside files).</summary>
    public const string Active = "active";

    /// <summary>A PDF with scripts, actions or embedded files.</summary>
    public const string PdfActive = "pdf-active";

    /// <summary>The file was repaired while it was read: what is shown is what could be read.</summary>
    public const string Incomplete = "incomplete";

    /// <summary>An XFA form, which draws little or nothing from its pages.</summary>
    public const string Xfa = "xfa";

    /// <summary>More pages than are shown (2,000).</summary>
    public const string ManyPages = "many-pages";
}

/// <summary>rc.29 (AC-17): which viewer opens a file, judged by its name and first bytes.</summary>
public static class FileKinds
{
    /// <summary>A PDF.</summary>
    public const string Pdf = "pdf";

    /// <summary>A Word document (.docx).</summary>
    public const string Word = "word";

    /// <summary>A workbook (.xlsx).</summary>
    public const string Sheet = "sheet";

    /// <summary>A picture the browser draws (PNG, JPEG, GIF, WebP, BMP).</summary>
    public const string Picture = "picture";

    /// <summary>A ZIP archive.</summary>
    public const string Archive = "archive";

    /// <summary>A DICOM file.</summary>
    public const string Dicom = "dicom";

    /// <summary>
    /// The kind of a file by its name and declared type; null when no viewer opens it. DICOM files are recognised
    /// by their bytes ("DICM" at 128), not their name; call <see cref="IsDicom"/> for those.
    /// </summary>
    /// <param name="name">The file's name.</param>
    /// <param name="contentType">Its declared type; may be empty.</param>
    /// <returns>The kind, or null.</returns>
    public static string? Of(string name, string? contentType)
    {
        ArgumentNullException.ThrowIfNull(name);
        string ct = (contentType ?? string.Empty).ToLowerInvariant();
        string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
        return (ct, ext) switch
        {
            (_, ".pdf") or ("application/pdf", _) => Pdf,
            (_, ".docx") or ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", _) => Word,
            (_, ".xlsx") or ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", _) => Sheet,
            (_, ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp") => Picture,
            ("image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/bmp", _) => Picture,
            (_, ".zip") or ("application/zip" or "application/x-zip-compressed", _) => Archive,
            (_, ".dcm") or ("application/dicom", _) => Dicom,
            _ => null,
        };
    }

    /// <summary>Whether a file's bytes carry the DICOM mark ("DICM" after a 128-byte preamble).</summary>
    /// <param name="bytes">The file's bytes.</param>
    /// <returns>True for DICOM.</returns>
    public static bool IsDicom(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 132 && bytes[128] == (byte)'D' && bytes[129] == (byte)'I' && bytes[130] == (byte)'C' && bytes[131] == (byte)'M';

    /// <summary>The type of a picture by its first bytes; null when it is not one the browser is given.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>"image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp", or null.</returns>
    public static string? PictureType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G')
        {
            return "image/png";
        }
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }
        if (bytes.Length >= 6 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'8')
        {
            return "image/gif";
        }
        if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            return "image/webp";
        }
        if (bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
        {
            return "image/bmp";
        }
        return null;
    }
}
