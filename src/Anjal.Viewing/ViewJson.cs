using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anjal.Viewing;

/// <summary>
/// rc.29: the shapes that cross the socket, written and read without reflection, so the viewer can be published
/// trimmed (AC-12).
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ViewRequest))]
[JsonSerializable(typeof(ViewAnswer))]
[JsonSerializable(typeof(PdfInfo))]
[JsonSerializable(typeof(PageText))]
[JsonSerializable(typeof(FindResult))]
[JsonSerializable(typeof(WordView))]
[JsonSerializable(typeof(SheetView))]
[JsonSerializable(typeof(ArchiveView))]
[JsonSerializable(typeof(FileOut))]
[JsonSerializable(typeof(DicomView))]
internal sealed partial class ViewJsonContext : JsonSerializerContext
{
}

/// <summary>rc.29: JSON for the shapes that cross the socket.</summary>
public static class ViewJson
{
    /// <summary>Writes a request.</summary>
    /// <param name="value">The request.</param>
    /// <returns>The JSON.</returns>
    public static string Write(ViewRequest value) => JsonSerializer.Serialize(value, ViewJsonContext.Default.ViewRequest);

    /// <summary>Writes an answer.</summary>
    /// <param name="value">The answer.</param>
    /// <returns>The JSON.</returns>
    public static string Write(ViewAnswer value) => JsonSerializer.Serialize(value, ViewJsonContext.Default.ViewAnswer);

    /// <summary>Writes a PDF's information.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The JSON.</returns>
    public static string Write(PdfInfo value) => JsonSerializer.Serialize(value, ViewJsonContext.Default.PdfInfo);

    /// <summary>Writes a page's text.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The JSON.</returns>
    public static string Write(PageText value) => JsonSerializer.Serialize(value, ViewJsonContext.Default.PageText);

    /// <summary>Writes what Find found.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The JSON.</returns>
    public static string Write(FindResult value) => JsonSerializer.Serialize(value, ViewJsonContext.Default.FindResult);

    /// <summary>Writes a Word document's text view.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The JSON.</returns>
    public static string Write(WordView value) => JsonSerializer.Serialize(value, ViewJsonContext.Default.WordView);

    /// <summary>Writes a sheet.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The JSON.</returns>
    public static string Write(SheetView value) => JsonSerializer.Serialize(value, ViewJsonContext.Default.SheetView);

    /// <summary>Writes an archive's listing.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The JSON.</returns>
    public static string Write(ArchiveView value) => JsonSerializer.Serialize(value, ViewJsonContext.Default.ArchiveView);

    /// <summary>Writes a file's name and type.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The JSON.</returns>
    public static string Write(FileOut value) => JsonSerializer.Serialize(value, ViewJsonContext.Default.FileOut);

    /// <summary>Writes a DICOM preview's caption.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The JSON.</returns>
    public static string Write(DicomView value) => JsonSerializer.Serialize(value, ViewJsonContext.Default.DicomView);

    /// <summary>Reads a DICOM preview's caption.</summary>
    /// <param name="json">The JSON.</param>
    /// <returns>The value.</returns>
    public static DicomView? ReadDicom(string? json) => json is null ? null : JsonSerializer.Deserialize(json, ViewJsonContext.Default.DicomView);

    /// <summary>Reads a request.</summary>
    /// <param name="json">The JSON.</param>
    /// <returns>The request.</returns>
    public static ViewRequest ReadRequest(string json) => JsonSerializer.Deserialize(json, ViewJsonContext.Default.ViewRequest) ?? throw new InvalidDataException("empty request");

    /// <summary>Reads an answer.</summary>
    /// <param name="json">The JSON.</param>
    /// <returns>The answer.</returns>
    public static ViewAnswer ReadAnswer(string json) => JsonSerializer.Deserialize(json, ViewJsonContext.Default.ViewAnswer) ?? throw new InvalidDataException("empty answer");

    /// <summary>Reads a PDF's information.</summary>
    /// <param name="json">The JSON.</param>
    /// <returns>The value.</returns>
    public static PdfInfo? ReadPdfInfo(string? json) => json is null ? null : JsonSerializer.Deserialize(json, ViewJsonContext.Default.PdfInfo);

    /// <summary>Reads a page's text.</summary>
    /// <param name="json">The JSON.</param>
    /// <returns>The value.</returns>
    public static PageText? ReadPageText(string? json) => json is null ? null : JsonSerializer.Deserialize(json, ViewJsonContext.Default.PageText);

    /// <summary>Reads what Find found.</summary>
    /// <param name="json">The JSON.</param>
    /// <returns>The value.</returns>
    public static FindResult? ReadFind(string? json) => json is null ? null : JsonSerializer.Deserialize(json, ViewJsonContext.Default.FindResult);

    /// <summary>Reads a Word document's text view.</summary>
    /// <param name="json">The JSON.</param>
    /// <returns>The value.</returns>
    public static WordView? ReadWord(string? json) => json is null ? null : JsonSerializer.Deserialize(json, ViewJsonContext.Default.WordView);

    /// <summary>Reads a sheet.</summary>
    /// <param name="json">The JSON.</param>
    /// <returns>The value.</returns>
    public static SheetView? ReadSheet(string? json) => json is null ? null : JsonSerializer.Deserialize(json, ViewJsonContext.Default.SheetView);

    /// <summary>Reads an archive's listing.</summary>
    /// <param name="json">The JSON.</param>
    /// <returns>The value.</returns>
    public static ArchiveView? ReadArchive(string? json) => json is null ? null : JsonSerializer.Deserialize(json, ViewJsonContext.Default.ArchiveView);

    /// <summary>Reads a file's name and type.</summary>
    /// <param name="json">The JSON.</param>
    /// <returns>The value.</returns>
    public static FileOut? ReadFile(string? json) => json is null ? null : JsonSerializer.Deserialize(json, ViewJsonContext.Default.FileOut);
}
