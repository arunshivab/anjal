using Anjal.Viewing;

namespace Anjal.Viewer;

/// <summary>rc.29: what a job in a child process gives back - a status, JSON, and files (pictures, a cleaned PDF).</summary>
/// <param name="Answer">The answer.</param>
/// <param name="Files">The files.</param>
public sealed record JobAnswer(ViewAnswer Answer, IReadOnlyList<byte[]> Files)
{
    /// <summary>Only a status (and, for the log, the library and exception type).</summary>
    /// <param name="status">The status.</param>
    /// <param name="detail">For the log; never the file's content.</param>
    /// <returns>The answer.</returns>
    public static JobAnswer Status(string status, string? detail) => new(new ViewAnswer { Status = status, Detail = detail }, Array.Empty<byte[]>());

    /// <summary>Done, with JSON.</summary>
    /// <param name="json">The JSON.</param>
    /// <returns>The answer.</returns>
    public static JobAnswer Json(string json) => new(new ViewAnswer { Json = json }, Array.Empty<byte[]>());

    /// <summary>Done, with JSON and files.</summary>
    /// <param name="json">The JSON.</param>
    /// <param name="files">The files.</param>
    /// <returns>The answer.</returns>
    public static JobAnswer Json(string json, IReadOnlyList<byte[]> files) => new(new ViewAnswer { Json = json }, files);

    /// <summary>Done, with one file (and JSON about it, or none).</summary>
    /// <param name="file">The file.</param>
    /// <param name="json">The JSON, or null.</param>
    /// <returns>The answer.</returns>
    public static JobAnswer File(byte[] file, string? json) => new(new ViewAnswer { Json = json }, new[] { file });
}
