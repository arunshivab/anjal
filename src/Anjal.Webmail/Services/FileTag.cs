namespace Anjal.Webmail.Services;

/// <summary>
/// The small tag that says what kind of file an attachment is (design review 2c, 11 Oct 2026):
/// PDF, JPG/PNG/IMG, DICOM, DOC, XLS, PPT, ZIP, or the extension in capitals (at most four letters).
/// The kind names the tag's colours in app.css (<c>.ftag-pdf</c> and so on).
/// </summary>
public static class FileTag
{
    /// <summary>The tag's words and kind for a file.</summary>
    /// <param name="fileName">The file's name.</param>
    /// <param name="contentType">Its MIME type.</param>
    /// <returns>The words shown (such as "PDF") and the kind (pdf, img, dicom, doc, xls, ppt, zip or other).</returns>
    public static (string Tag, string Kind) Of(string? fileName, string? contentType)
    {
        string name = fileName ?? string.Empty;
        string type = (contentType ?? string.Empty).ToLowerInvariant();
        string ext = System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if (type == "application/pdf" || ext == "pdf")
        {
            return ("PDF", "pdf");
        }
        if (type == "application/dicom" || ext == "dcm" || name.Equals("DICOMDIR", StringComparison.OrdinalIgnoreCase))
        {
            return ("DICOM", "dicom");
        }
        if (type.StartsWith("image/", StringComparison.Ordinal) || ext is "jpg" or "jpeg" or "png" or "gif" or "webp" or "bmp" or "tif" or "tiff" or "heic")
        {
            return (ext is "jpg" or "jpeg" ? "JPG" : ext == "png" ? "PNG" : "IMG", "img");
        }
        if (ext is "doc" or "docx" or "odt" or "rtf")
        {
            return ("DOC", "doc");
        }
        if (ext is "xls" or "xlsx" or "ods" or "csv")
        {
            return ("XLS", "xls");
        }
        if (ext is "ppt" or "pptx" or "odp")
        {
            return ("PPT", "ppt");
        }
        if (ext is "zip" or "7z" or "rar" or "gz" or "tgz" or "tar" or "bz2" or "xz")
        {
            return ("ZIP", "zip");
        }
        string shown = ext.Length == 0 ? "FILE" : ext.ToUpperInvariant();
        return (shown.Length > 4 ? shown[..4] : shown, "other");
    }
}
