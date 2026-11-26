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

    /// <summary>rc.31 (D4): a CSV or TSV file as a plain table (10 MB, 5,000 rows, 200 columns, 1,000 characters a cell).</summary>
    public const string Csv = "csv";

    /// <summary>What is inside an archive (AC-17).</summary>
    public const string ArchiveList = "archive.list";

    /// <summary>One file from inside an archive, for its Download (never a program).</summary>
    public const string ArchiveEntry = "archive.entry";

    /// <summary>A picture checked by its bytes and given back as itself (inside an archive).</summary>
    public const string Picture = "picture";

    /// <summary>A DICOM file's preview (rc.30: SIAARA v0.1.0-rc.1; shown only with ANJAL_VIEWER_DICOM=on, D-372).</summary>
    public const string Dicom = "dicom";

    /// <summary>rc.30 (D-366): a DICOM file's patient name and ID, for Show patient details only.</summary>
    public const string DicomPatient = "dicom.patient";

    /// <summary>
    /// rc.30 (AC-19, D-386): a ZIP's DICOM files grouped by patient, study and series. rc.31 (lite DICOM): run in a
    /// child of its own like every DICOM job (a 256 MiB heap, stopped past 320 MB), one grouping at a time.
    /// </summary>
    public const string DicomGroup = "dicom.group";

    /// <summary>rc.31 (lite DICOM): whether a kind is a DICOM job, run in a child of its own at 320 MB.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>True for <see cref="Dicom"/>, <see cref="DicomPatient"/> and <see cref="DicomGroup"/>.</returns>
    public static bool IsDicom(string? kind) => kind is Dicom or DicomPatient or DicomGroup;

    /// <summary>rc.29 part 2 (DES-22 §7.8, 1): what the editor must know first - signatures, a password, forms.</summary>
    public const string PdfEditCheck = "pdf.edit.check";

    /// <summary>rc.29 part 2 (DES-22 §7.7): the pages in their new order and turn, as a PDF the viewer's jobs can show.</summary>
    public const string PdfArrange = "pdf.edit.arrange";

    /// <summary>rc.29 part 2 (DES-22 §7.1): every change applied in one pass, checked, and the new PDF given back.</summary>
    public const string PdfSave = "pdf.edit.save";

    /// <summary>rc.29 part 2 (DES-22 §7.6): the patient signs in a PDF's text, each with its boxes, to redact or not.</summary>
    public const string PdfSigns = "pdf.edit.signs";

    /// <summary>
    /// A DMARC aggregate report attached by a receiver (rc.15, items 31 to 33), unpacked and read here like every
    /// other outside file (D-405, decision A of 17 Nov 2026).
    /// </summary>
    public const string DmarcReport = "report.dmarc";

    /// <summary>An SMTP TLS report attached by a receiver (RFC 8460; rc.15, item 59), read here (D-405, decision A).</summary>
    public const string TlsReport = "report.tls";

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
        Sheet or Csv => TimeSpan.FromSeconds(8),
        Dicom or DicomPatient => TimeSpan.FromSeconds(8),
        DicomGroup => TimeSpan.FromSeconds(15),
        PdfEditCheck => TimeSpan.FromSeconds(10),
        PdfArrange => TimeSpan.FromSeconds(30),
        PdfSave => TimeSpan.FromSeconds(90),
        PdfSigns => TimeSpan.FromSeconds(20),
        DmarcReport or TlsReport => TimeSpan.FromSeconds(10),
        _ when kind.StartsWith("test.", StringComparison.Ordinal) => TimeSpan.FromSeconds(3),
        _ => TimeSpan.FromSeconds(8),
    };
}
