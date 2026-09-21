using System.Text;

namespace Anjal.Mime;

/// <summary>
/// Parses a sequence of bytes into a <see cref="MimeMessage"/>. The parser
/// is line-oriented and tolerates either CRLF or bare LF line endings.
/// It is pragmatic about real-world input: malformed individual headers or
/// truncated multipart bodies are accepted, with as much of the structure
/// preserved as possible.
/// </summary>
public static class MimeParser
{
    /// <summary>
    /// Parse a byte array into a message.
    /// </summary>
    /// <param name="raw">The raw RFC 5322 / MIME bytes.</param>
    /// <returns>The parsed message.</returns>
    public static MimeMessage Parse(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var budget = new PartBudget();
        return ParseEntity(raw, 0, raw.Length, isTopLevel: true, depth: 0, budget) is MimeMessage msg
            ? msg
            : new MimeMessage(new MimePart());
    }

    /// <summary>
    /// Parse a UTF-8 encoded string into a message. Convenience overload.
    /// </summary>
    /// <param name="text">The message text.</param>
    /// <returns>The parsed message.</returns>
    public static MimeMessage Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Parse(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// Parse from a stream by reading it to the end first.
    /// </summary>
    /// <param name="stream">The input stream.</param>
    /// <returns>The parsed message.</returns>
    public static MimeMessage Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Parse(ms.ToArray());
    }

    /// <summary>
    /// The deepest multipart nesting accepted. Real mail rarely exceeds
    /// five or six levels; thirty-two leaves ample room while keeping the
    /// recursion far from the stack limit. Without a bound, a 7 MB message
    /// of nested multiparts - well under the size limit - overflows the
    /// stack, which no catch block can intercept, and takes the whole
    /// server process down.
    /// </summary>
    public const int MaxNestingDepth = 32;

    /// <summary>
    /// The most MIME parts accepted in one message, across every level.
    /// Bounds the work a single message can cause even when it stays
    /// shallow.
    /// </summary>
    public const int MaxParts = 1000;

    /// <summary>Counts parts across the whole parse, shared by every level.</summary>
    private sealed class PartBudget
    {
        public int Parts;
    }

    private static object ParseEntity(byte[] raw, int start, int end, bool isTopLevel, int depth, PartBudget budget)
    {
        if (depth > MaxNestingDepth)
        {
            throw new MimeParseException($"Multipart nesting exceeds {MaxNestingDepth} levels.");
        }
        if (++budget.Parts > MaxParts)
        {
            throw new MimeParseException($"Message has more than {MaxParts} MIME parts.");
        }

        // 1. Find the blank line separating headers from body.
        int headerEnd = FindHeaderBodySeparator(raw, start, end);
        int bodyStart = headerEnd < 0 ? end : headerEnd;

        // 2. Parse headers.
        var headers = ParseHeaders(raw, start, bodyStart);

        // 3. Skip past the blank-line separator into the body.
        int bodyContentStart = SkipBlankLine(raw, bodyStart, end);

        // 4. Look at Content-Type to decide whether body is multipart or leaf.
        string? ctValue = null;
        foreach (var h in headers)
        {
            if (string.Equals(h.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                ctValue = h.Value;
                break;
            }
        }
        var ct = ContentType.Parse(ctValue);

        MimeEntity entity;
        if (ct.IsMultipart && !string.IsNullOrEmpty(ct.Boundary))
        {
            var multi = new MimeMultipart();
            CopyHeaders(headers, multi.Headers);
            ParseMultipart(raw, bodyContentStart, end, ct.Boundary!, multi, depth + 1, budget);
            entity = multi;
        }
        else
        {
            var part = new MimePart();
            CopyHeaders(headers, part.Headers);
            part.Body = DecodeBody(raw, bodyContentStart, end, part.ContentTransferEncoding);
            entity = part;
        }

        return isTopLevel ? new MimeMessage(entity) : entity;
    }

    private static void CopyHeaders(List<MimeHeader> source, HeaderCollection target)
    {
        foreach (var h in source)
        {
            target.Add(h);
        }
    }

    private static List<MimeHeader> ParseHeaders(byte[] raw, int start, int end)
    {
        var result = new List<MimeHeader>();
        int i = start;
        while (i < end)
        {
            int lineEnd = FindLineEnd(raw, i, end);
            if (lineEnd == i)
            {
                // Empty line - header section is done. Shouldn't normally reach
                // here because the caller already located the separator.
                break;
            }

            // Read the header line plus any continuation lines (RFC 5322 section 2.2.3).
            var sb = new StringBuilder();
            sb.Append(DecodeAscii(raw, i, lineEnd));
            int next = SkipLineEnding(raw, lineEnd, end);
            while (next < end && (raw[next] == ' ' || raw[next] == '\t'))
            {
                int contEnd = FindLineEnd(raw, next, end);
                sb.Append(' ');
                sb.Append(DecodeAscii(raw, next, contEnd).Trim());
                next = SkipLineEnding(raw, contEnd, end);
            }
            i = next;

            string line = sb.ToString();
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                // Malformed header - skip it rather than failing the whole parse.
                continue;
            }

            string name = line.Substring(0, colon).Trim();
            string value = line.Substring(colon + 1).TrimStart();

            // Incoming mail is kept, not refused: a field name that is not a
            // valid token is dropped, and a stray CR, LF or NUL left in a
            // value after unfolding becomes a space.
            if (!MimeHeader.IsValidName(name))
            {
                continue;
            }
            result.Add(new MimeHeader(name, MimeHeader.Neutralise(value)));
        }
        return result;
    }

    private static void ParseMultipart(byte[] raw, int start, int end, string boundary, MimeMultipart multi, int depth, PartBudget budget)
    {
        byte[] dashBoundary = Encoding.ASCII.GetBytes("--" + boundary);
        var positions = FindBoundaries(raw, start, end, dashBoundary);
        if (positions.Count == 0)
        {
            // No boundary found at all - preserve the whole body as preamble.
            multi.Preamble = DecodeAscii(raw, start, end);
            return;
        }

        // Preamble: bytes before the first boundary.
        if (positions[0] > start)
        {
            int preambleEnd = positions[0];
            // Trim trailing CRLF that belongs to the boundary delimiter.
            preambleEnd = TrimTrailingLineEnd(raw, start, preambleEnd);
            multi.Preamble = DecodeAscii(raw, start, preambleEnd);
        }

        for (int p = 0; p < positions.Count; p++)
        {
            int boundaryStart = positions[p];
            int afterBoundaryLine = SkipPastLine(raw, boundaryStart, end);

            // If the boundary is followed by "--" it's the closing boundary.
            // Anything after is epilogue.
            if (IsClosingBoundary(raw, boundaryStart, dashBoundary, end))
            {
                int epilogueStart = afterBoundaryLine;
                if (epilogueStart < end)
                {
                    multi.Epilogue = DecodeAscii(raw, epilogueStart, end);
                }
                break;
            }

            int partStart = afterBoundaryLine;
            int partEnd = p + 1 < positions.Count ? positions[p + 1] : end;
            int trimmedEnd = TrimTrailingLineEnd(raw, partStart, partEnd);

            object child = ParseEntity(raw, partStart, trimmedEnd, isTopLevel: false, depth, budget);
            if (child is MimeEntity entity)
            {
                multi.Parts.Add(entity);
            }
        }
    }

    private static List<int> FindBoundaries(byte[] raw, int start, int end, byte[] dashBoundary)
    {
        var result = new List<int>();
        int i = start;
        while (i < end)
        {
            // Boundary must be at the start of a line (RFC 2046 section 5.1.1).
            bool atLineStart = i == 0 || raw[i - 1] == '\n';
            if (atLineStart && MatchesAt(raw, i, end, dashBoundary))
            {
                result.Add(i);
                i += dashBoundary.Length;
            }
            else
            {
                i++;
            }
        }
        return result;
    }

    private static bool IsClosingBoundary(byte[] raw, int boundaryStart, byte[] dashBoundary, int end)
    {
        int afterBoundary = boundaryStart + dashBoundary.Length;
        return afterBoundary + 1 < end && raw[afterBoundary] == '-' && raw[afterBoundary + 1] == '-';
    }

    private static bool MatchesAt(byte[] raw, int pos, int end, byte[] needle)
    {
        if (pos + needle.Length > end)
        {
            return false;
        }
        for (int i = 0; i < needle.Length; i++)
        {
            if (raw[pos + i] != needle[i])
            {
                return false;
            }
        }
        return true;
    }

    private static int SkipPastLine(byte[] raw, int pos, int end)
    {
        int lineEnd = FindLineEnd(raw, pos, end);
        return SkipLineEnding(raw, lineEnd, end);
    }

    private static int TrimTrailingLineEnd(byte[] raw, int start, int end)
    {
        int e = end;
        if (e > start && raw[e - 1] == '\n')
        {
            e--;
            if (e > start && raw[e - 1] == '\r')
            {
                e--;
            }
        }
        return e;
    }

    private static byte[] DecodeBody(byte[] raw, int start, int end, ContentTransferEncoding encoding)
    {
        if (start >= end)
        {
            return Array.Empty<byte>();
        }

        // For multipart bodies (handled elsewhere) and these encodings we
        // pass through raw. For base64 / quoted-printable we decode.
        switch (encoding)
        {
            case ContentTransferEncoding.Base64:
                {
                    string asString = Encoding.ASCII.GetString(raw, start, end - start);
                    try
                    {
                        return Base64Codec.Decode(asString);
                    }
                    catch (FormatException)
                    {
                        return Slice(raw, start, end);
                    }
                }

            case ContentTransferEncoding.QuotedPrintable:
                {
                    string asString = Encoding.ASCII.GetString(raw, start, end - start);
                    return QuotedPrintableCodec.Decode(asString);
                }

            default:
                return Slice(raw, start, end);
        }
    }

    private static byte[] Slice(byte[] raw, int start, int end)
    {
        int len = end - start;
        if (len <= 0)
        {
            return Array.Empty<byte>();
        }
        var result = new byte[len];
        Array.Copy(raw, start, result, 0, len);
        return result;
    }

    private static int FindHeaderBodySeparator(byte[] raw, int start, int end)
    {
        // The separator is an empty line: CRLF CRLF or LF LF.
        int i = start;
        while (i < end)
        {
            if (i + 1 < end && raw[i] == '\r' && raw[i + 1] == '\n')
            {
                if (i + 3 < end && raw[i + 2] == '\r' && raw[i + 3] == '\n')
                {
                    return i;
                }
                if (i + 2 < end && raw[i + 2] == '\n')
                {
                    return i;
                }
                i += 2;
                continue;
            }
            if (raw[i] == '\n')
            {
                if (i + 1 < end && raw[i + 1] == '\n')
                {
                    return i;
                }
                if (i + 2 < end && raw[i + 1] == '\r' && raw[i + 2] == '\n')
                {
                    return i;
                }
                i++;
                continue;
            }
            i++;
        }
        return -1;
    }

    private static int SkipBlankLine(byte[] raw, int pos, int end)
    {
        if (pos >= end)
        {
            return end;
        }
        // Skip the blank-line separator (CRLF CRLF or LF LF or mixed).
        int p = SkipLineEnding(raw, pos, end);
        p = SkipLineEnding(raw, p, end);
        return p;
    }

    private static int FindLineEnd(byte[] raw, int start, int end)
    {
        int i = start;
        while (i < end && raw[i] != '\r' && raw[i] != '\n')
        {
            i++;
        }
        return i;
    }

    private static int SkipLineEnding(byte[] raw, int pos, int end)
    {
        if (pos < end && raw[pos] == '\r')
        {
            pos++;
        }
        if (pos < end && raw[pos] == '\n')
        {
            pos++;
        }
        return pos;
    }

    private static string DecodeAscii(byte[] raw, int start, int end)
    {
        if (start >= end)
        {
            return string.Empty;
        }
        return Encoding.UTF8.GetString(raw, start, end - start);
    }
}
