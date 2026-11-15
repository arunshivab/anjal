namespace Anjal.Viewing;

/// <summary>rc.29 (DES-22 §4): the jobs the viewer process does.</summary>
public static class ViewKinds
{
    /// <summary>A PDF's pages, sizes, outline and notices.</summary>
    public const string PdfInfo = "pdf.info";

    /// <summary>One PDF page drawn as a PNG.</summary>
    public const string PdfPage = "pdf.page";

    /// <summary>One PDF page's text and where it sits.</summary>
    public const string PdfText = "pdf.text";

    /// <summary>Words found in a PDF.</summary>
    public const string PdfFind = "pdf.find";

    /// <summary>A copy of a PDF without its scripts, actions and embedded files (D-367).</summary>
    public const string PdfClean = "pdf.clean";

    /// <summary>A Word document as text, headings, lists, tables and pictures (AC-06).</summary>
    public const string Word = "word";

    /// <summary>One sheet of a workbook (2,000 rows at most, D-361).</summary>
    public const string Sheet = "sheet";

    /// <summary>What is inside an archive (AC-17).</summary>
    public const string ArchiveList = "archive.list";

    /// <summary>One file from inside an archive, for its Download (never a program).</summary>
    public const string ArchiveEntry = "archive.entry";

    /// <summary>A picture checked by its bytes and given back as itself (inside an archive).</summary>
    public const string Picture = "picture";

    /// <summary>A DICOM file's preview (switched off until the Siaara package is on the feed, D-372).</summary>
    public const string Dicom = "dicom";

    /// <summary>
    /// How long the job may take before its child process is stopped (DES-22 §4.1): open and the first page 10 s,
    /// another page 5 s, a sheet 8 s, Word 15 s, DICOM 8 s, an editor save 60 s.
    /// </summary>
    /// <param name="kind">The job.</param>
    /// <param name="page">The page, for a PDF page.</param>
    /// <returns>The deadline.</returns>
    public static TimeSpan Deadline(string kind, int page = 0) => kind switch
    {
        PdfInfo => TimeSpan.FromSeconds(10),
        PdfPage => TimeSpan.FromSeconds(page == 0 ? 10 : 5),
        PdfText => TimeSpan.FromSeconds(5),
        PdfFind => TimeSpan.FromSeconds(15),
        PdfClean => TimeSpan.FromSeconds(30),
        Word => TimeSpan.FromSeconds(15),
        Sheet => TimeSpan.FromSeconds(8),
        Dicom => TimeSpan.FromSeconds(8),
        _ when kind.StartsWith("test.", StringComparison.Ordinal) => TimeSpan.FromSeconds(3),
        _ => TimeSpan.FromSeconds(8),
    };
}
