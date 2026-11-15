namespace Anjal.Viewing;

/// <summary>
/// rc.29 (DES-22 §4.2): one request to the viewer process. The file's bytes travel beside it; nothing about the
/// person or the organisation does - <see cref="Who"/> is an opaque key, used only to share the viewer fairly.
/// </summary>
public sealed class ViewRequest
{
    /// <summary>What to do: one of the <see cref="ViewKinds"/>.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>The file's name as the mail gave it.</summary>
    public string? Name { get; init; }

    /// <summary>The file's declared type.</summary>
    public string? ContentType { get; init; }

    /// <summary>The file's password, for this request only; never kept.</summary>
    public string? Password { get; init; }

    /// <summary>
    /// AC-20: for a file inside an archive (<see cref="Entry"/> set), the password of the attachment itself - the
    /// archive - for this request only; never kept.
    /// </summary>
    public string? ArchivePassword { get; init; }

    /// <summary>
    /// AC-20: for a file inside an archive inside the archive ("3/5"), the password of that inner archive ("3"),
    /// for this request only; never kept.
    /// </summary>
    public string? InnerArchivePassword { get; init; }

    /// <summary>A page (0-based) for a PDF page, its text, or the first page of a search.</summary>
    public int Page { get; init; }

    /// <summary>The width in pixels to draw a page at.</summary>
    public int Width { get; init; }

    /// <summary>The words to find.</summary>
    public string? Query { get; init; }

    /// <summary>A sheet (0-based) of a workbook.</summary>
    public int Sheet { get; init; }

    /// <summary>
    /// A file inside an archive: the entry's place, and for an archive inside the archive the places one after
    /// another ("3", "3/1"). Null for the file itself.
    /// </summary>
    public string? Entry { get; init; }

    /// <summary>The reader's language and region ("en-IN", "ta-IN"), for number and date formats.</summary>
    public string? Culture { get; init; }

    /// <summary>An opaque key for the person asking, so one person cannot take the whole viewer.</summary>
    public string? Who { get; init; }
}
