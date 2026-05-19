using System.Text;

namespace Anjal.Mime;

/// <summary>
/// Serialises a <see cref="MimeMessage"/> back to wire-format bytes.
/// Always emits CRLF line endings. Long header lines are folded at 78 columns
/// where possible (RFC 5322 section 2.1.1 SHOULD limit).
/// </summary>
public static class MimeBuilder
{
    private const int RecommendedLineLength = 78;

    /// <summary>
    /// Serialise the given message to bytes.
    /// </summary>
    /// <param name="message">The message to serialise.</param>
    /// <returns>The wire-format bytes.</returns>
    public static byte[] Build(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var ms = new MemoryStream();
        WriteEntity(ms, message.Body);
        return ms.ToArray();
    }

    /// <summary>
    /// Serialise the given message to a stream.
    /// </summary>
    /// <param name="message">The message to serialise.</param>
    /// <param name="output">The destination stream.</param>
    public static void Build(MimeMessage message, Stream output)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(output);
        WriteEntity(output, message.Body);
    }

    private static void WriteEntity(Stream output, MimeEntity entity)
    {
        WriteHeaders(output, entity);
        WriteCrLf(output);

        switch (entity)
        {
            case MimePart part:
                WritePartBody(output, part);
                break;
            case MimeMultipart multi:
                WriteMultipartBody(output, multi);
                break;
            default:
                throw new InvalidOperationException($"Unsupported MimeEntity type: {entity.GetType().Name}");
        }
    }

    private static void WriteHeaders(Stream output, MimeEntity entity)
    {
        foreach (var header in entity.Headers)
        {
            string line = $"{header.Name}: {header.Value}";
            string folded = FoldHeaderLine(line);
            WriteAscii(output, folded);
            WriteCrLf(output);
        }
    }

    private static void WritePartBody(Stream output, MimePart part)
    {
        if (part.Body.Length == 0)
        {
            return;
        }

        switch (part.ContentTransferEncoding)
        {
            case ContentTransferEncoding.Base64:
                {
                    string b64 = Base64Codec.Encode(part.Body);
                    WriteAscii(output, b64);
                    WriteCrLf(output);
                    break;
                }
            case ContentTransferEncoding.QuotedPrintable:
                {
                    string qp = QuotedPrintableCodec.Encode(part.Body);
                    WriteAscii(output, qp);
                    if (!qp.EndsWith("\r\n", StringComparison.Ordinal))
                    {
                        WriteCrLf(output);
                    }
                    break;
                }
            default:
                output.Write(part.Body, 0, part.Body.Length);
                if (!EndsWithCrLf(part.Body))
                {
                    WriteCrLf(output);
                }
                break;
        }
    }

    private static void WriteMultipartBody(Stream output, MimeMultipart multi)
    {
        string? boundary = multi.ContentType.Boundary;
        if (string.IsNullOrEmpty(boundary))
        {
            throw new InvalidOperationException("Multipart entity has no boundary parameter on Content-Type.");
        }

        if (!string.IsNullOrEmpty(multi.Preamble))
        {
            WriteAscii(output, multi.Preamble);
            WriteCrLf(output);
        }

        foreach (var child in multi.Parts)
        {
            WriteAscii(output, "--" + boundary);
            WriteCrLf(output);
            WriteEntity(output, child);
        }

        WriteAscii(output, "--" + boundary + "--");
        WriteCrLf(output);

        if (!string.IsNullOrEmpty(multi.Epilogue))
        {
            WriteAscii(output, multi.Epilogue);
        }
    }

    private static bool EndsWithCrLf(byte[] body)
    {
        if (body.Length < 2)
        {
            return false;
        }
        return body[body.Length - 2] == (byte)'\r' && body[body.Length - 1] == (byte)'\n';
    }

    private static string FoldHeaderLine(string line)
    {
        if (line.Length <= RecommendedLineLength)
        {
            return line;
        }

        var sb = new StringBuilder(line.Length + (line.Length / RecommendedLineLength * 3));
        int lineStart = 0;

        while (lineStart < line.Length)
        {
            int remaining = line.Length - lineStart;
            if (remaining <= RecommendedLineLength)
            {
                sb.Append(line, lineStart, remaining);
                break;
            }

            // Find a space to fold at, scanning backwards from the limit.
            int breakAt = -1;
            int searchEnd = lineStart + RecommendedLineLength;
            for (int i = searchEnd; i > lineStart + 1; i--)
            {
                if (line[i] == ' ')
                {
                    breakAt = i;
                    break;
                }
            }

            if (breakAt < 0)
            {
                // Can't fold within the recommended length - find the next space.
                int next = line.IndexOf(' ', searchEnd);
                if (next < 0)
                {
                    // No more spaces; emit the rest as a single line.
                    sb.Append(line, lineStart, line.Length - lineStart);
                    break;
                }
                breakAt = next;
            }

            sb.Append(line, lineStart, breakAt - lineStart);
            sb.Append("\r\n ");
            lineStart = breakAt + 1;
        }

        return sb.ToString();
    }

    private static void WriteAscii(Stream output, string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        output.Write(bytes, 0, bytes.Length);
    }

    private static void WriteCrLf(Stream output)
    {
        output.WriteByte((byte)'\r');
        output.WriteByte((byte)'\n');
    }
}
