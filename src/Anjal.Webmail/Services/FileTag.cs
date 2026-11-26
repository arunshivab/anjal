namespace Anjal.Webmail.Services;

/// <summary>
/// The small tag that says what kind of file an attachment is. rc.31 (D2, the owner, 25 Nov 2026): the tag shows the
/// file's real extension in capitals (PDF, DOCX, XLSX, CSV, NUPKG ...), at most five letters; a file with no extension
/// shows FILE (DICOMDIR shows DICOM, and a PDF or DICOM known only by its type shows PDF or DCM). The colour still
/// follows the family (the kind names the tag's colours in app.css, <c>.ftag-pdf</c> and so on), so related files
/// still look alike. app.js holds the same rule for files added in the browser (<c>fileTag</c>); a test keeps the two
/// the same.
/// </summary>
public static class FileTag
{
    /// <summary>The most letters a tag shows.</summary>
    public const int MaxLetters = 5;

    /// <summary>The tag's words and kind for a file.</summary>
    /// <param name="fileName">The file's name.</param>
    /// <param name="contentType">Its MIME type.</param>
    /// <returns>The words shown (such as "DOCX") and the kind (pdf, img, dicom, doc, xls, ppt, zip or other).</returns>
    public static (string Tag, string Kind) Of(string? fileName, string? contentType)
    {
        string name = fileName ?? string.Empty;
        string type = (contentType ?? string.Empty).ToLowerInvariant();
        string ext = System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        string kind = KindOf(ext, type, name);
        string shown;
        if (ext.Length > 0)
        {
            shown = ext.ToUpperInvariant();
        }
        else if (name.Equals("DICOMDIR", StringComparison.OrdinalIgnoreCase))
        {
            shown = "DICOM";
        }
        else if (kind == "pdf")
        {
            shown = "PDF";
        }
        else if (kind == "dicom")
        {
            shown = "DCM";
        }
        else
        {
            shown = "FILE";
        }
        return (shown.Length > MaxLetters ? shown[..MaxLetters] : shown, kind);
    }

    // The family, for the tag's colour.
    private static string KindOf(string ext, string type, string name)
    {
        if (type == "application/pdf" || ext == "pdf")
        {
            return "pdf";
        }
        if (type == "application/dicom" || ext == "dcm" || name.Equals("DICOMDIR", StringComparison.OrdinalIgnoreCase))
        {
            return "dicom";
        }
        if (type.StartsWith("image/", StringComparison.Ordinal) || ext is "jpg" or "jpeg" or "png" or "gif" or "webp" or "bmp" or "tif" or "tiff" or "heic")
        {
            return "img";
        }
        if (ext is "doc" or "docx" or "odt" or "rtf")
        {
            return "doc";
        }
        if (ext is "xls" or "xlsx" or "ods" or "csv" or "tsv")
        {
            return "xls";
        }
        if (ext is "ppt" or "pptx" or "odp")
        {
            return "ppt";
        }
        if (ext is "zip" or "7z" or "rar" or "gz" or "tgz" or "tar" or "bz2" or "xz")
        {
            return "zip";
        }
        return "other";
    }
}
