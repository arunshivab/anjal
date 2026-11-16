using System.Buffers.Binary;
using System.Text;

namespace Anjal.Viewing;

/// <summary>
/// rc.29 (DES-22 §4.2): how a request or an answer travels - over the socket between the webmail and the viewer,
/// and over the pipes between the viewer and a child. One frame: the mark "AVW1", the JSON's length and the JSON,
/// the number of files, and each file's length and bytes. Sizes are checked before anything is read into memory:
/// the JSON at most 4 MB, a file at most 64 MB (DES-22 §4.2), 64 files, 160 MB in all.
/// </summary>
public static class ViewFrames
{
    /// <summary>The most a file may weigh on either side (DES-22 §4.2): larger is not sent, and not accepted.</summary>
    public const int MaxFileBytes = 64 * 1024 * 1024;

    /// <summary>The most the JSON part may weigh.</summary>
    public const int MaxJsonBytes = 4 * 1024 * 1024;

    /// <summary>The most files one frame may carry.</summary>
    public const int MaxFiles = 64;

    /// <summary>The most one frame may weigh in all.</summary>
    public const long MaxFrameBytes = 160L * 1024 * 1024;

    private static readonly byte[] Mark = "AVW1"u8.ToArray();

    /// <summary>Writes one frame.</summary>
    /// <param name="output">Where to.</param>
    /// <param name="json">The JSON part.</param>
    /// <param name="files">The files.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task WriteAsync(Stream output, string json, IReadOnlyList<ReadOnlyMemory<byte>> files, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(files);
        byte[] text = Encoding.UTF8.GetBytes(json);
        if (text.Length > MaxJsonBytes || files.Count > MaxFiles || files.Any(f => f.Length > MaxFileBytes))
        {
            throw new InvalidDataException("frame too large");
        }
        byte[] head = new byte[12];
        Mark.CopyTo(head, 0);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(4), text.Length);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(8), files.Count);
        await output.WriteAsync(head, ct).ConfigureAwait(false);
        await output.WriteAsync(text, ct).ConfigureAwait(false);
        byte[] length = new byte[4];
        foreach (ReadOnlyMemory<byte> f in files)
        {
            BinaryPrimitives.WriteInt32LittleEndian(length, f.Length);
            await output.WriteAsync(length, ct).ConfigureAwait(false);
            await output.WriteAsync(f, ct).ConfigureAwait(false);
        }
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one frame; null when the stream ended before it began.</summary>
    /// <param name="input">Where from.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The JSON part and the files.</returns>
    /// <exception cref="InvalidDataException">The frame is not one, or is larger than allowed.</exception>
    public static async Task<(string Json, List<byte[]> Files)?> ReadAsync(Stream input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        byte[] head = new byte[12];
        int got = await FillAsync(input, head, ct).ConfigureAwait(false);
        if (got == 0)
        {
            return null;
        }
        if (got < head.Length || !head.AsSpan(0, 4).SequenceEqual(Mark))
        {
            throw new InvalidDataException("not a viewer frame");
        }
        int jsonLength = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4));
        int count = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(8));
        if (jsonLength < 0 || jsonLength > MaxJsonBytes || count < 0 || count > MaxFiles)
        {
            throw new InvalidDataException("frame too large");
        }
        byte[] text = new byte[jsonLength];
        if (await FillAsync(input, text, ct).ConfigureAwait(false) < jsonLength)
        {
            throw new InvalidDataException("frame cut short");
        }
        var files = new List<byte[]>(count);
        long total = jsonLength;
        byte[] length = new byte[4];
        for (int i = 0; i < count; i++)
        {
            if (await FillAsync(input, length, ct).ConfigureAwait(false) < 4)
            {
                throw new InvalidDataException("frame cut short");
            }
            int n = BinaryPrimitives.ReadInt32LittleEndian(length);
            total += n;
            if (n < 0 || n > MaxFileBytes || total > MaxFrameBytes)
            {
                throw new InvalidDataException("frame too large");
            }
            byte[] file = new byte[n];
            if (await FillAsync(input, file, ct).ConfigureAwait(false) < n)
            {
                throw new InvalidDataException("frame cut short");
            }
            files.Add(file);
        }
        return (Encoding.UTF8.GetString(text), files);
    }

    private static async Task<int> FillAsync(Stream input, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await input.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }
            read += n;
        }
        return read;
    }
}
