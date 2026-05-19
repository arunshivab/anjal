using System.Globalization;
using System.Text;

namespace Anjal.Mime;

/// <summary>
/// Quoted-Printable encoder and decoder per RFC 2045 section 6.7.
/// Designed for content that is mostly US-ASCII text with occasional
/// non-ASCII octets that need escaping.
/// </summary>
public static class QuotedPrintableCodec
{
    private const int MaxLineLength = 76;

    /// <summary>
    /// Decode a quoted-printable string back to its original bytes.
    /// Soft line breaks ("=\r\n" or "=\n") are removed, "=XX" hex escapes
    /// are converted to their byte value, and other characters pass through
    /// unchanged.
    /// </summary>
    /// <param name="input">The quoted-printable input.</param>
    /// <returns>The decoded bytes.</returns>
    public static byte[] Decode(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var output = new System.Collections.Generic.List<byte>(input.Length);
        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];
            if (c == '=')
            {
                // Soft line break: "=" at end of line, optionally with trailing whitespace
                // before the line break. RFC 2045 section 6.7 rule 5.
                int j = i + 1;
                while (j < input.Length && (input[j] == ' ' || input[j] == '\t'))
                {
                    j++;
                }
                if (j < input.Length && input[j] == '\r')
                {
                    j++;
                    if (j < input.Length && input[j] == '\n')
                    {
                        j++;
                    }
                    i = j;
                    continue;
                }
                if (j < input.Length && input[j] == '\n')
                {
                    i = j + 1;
                    continue;
                }

                // Hex escape "=XX". If the two following characters aren't both
                // hex digits we are tolerant and emit the '=' literally (a common
                // pragmatic choice; strict mode could throw).
                if (i + 2 < input.Length && IsHex(input[i + 1]) && IsHex(input[i + 2]))
                {
                    int hi = HexValue(input[i + 1]);
                    int lo = HexValue(input[i + 2]);
                    output.Add((byte)((hi << 4) | lo));
                    i += 3;
                    continue;
                }

                output.Add((byte)'=');
                i++;
                continue;
            }

            output.Add((byte)c);
            i++;
        }
        return [.. output];
    }

    /// <summary>
    /// Encode bytes as quoted-printable text. Lines are wrapped to 76
    /// characters using soft line breaks ("=\r\n") per RFC 2045 section 6.7
    /// rule 5. Existing CRLF in the input is preserved as hard line breaks.
    /// </summary>
    /// <param name="data">The bytes to encode.</param>
    /// <returns>The quoted-printable encoding.</returns>
    public static string Encode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var sb = new StringBuilder(data.Length);
        var line = new StringBuilder(MaxLineLength + 4);

        void FlushSoftBreak()
        {
            sb.Append(line);
            sb.Append("=\r\n");
            line.Clear();
        }

        void FlushHardBreak()
        {
            sb.Append(line);
            sb.Append("\r\n");
            line.Clear();
        }

        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];

            // Preserve existing CRLF as a hard line break.
            if (b == '\r' && i + 1 < data.Length && data[i + 1] == '\n')
            {
                // Trailing whitespace before CRLF must be encoded to survive
                // transport (RFC 2045 section 6.7 rule 3).
                EscapeTrailingWhitespace(line);
                FlushHardBreak();
                i++;
                continue;
            }

            string token = EncodeByte(b);

            // Need room for token + a possible "=" soft break marker.
            if (line.Length + token.Length > MaxLineLength - 1)
            {
                FlushSoftBreak();
            }
            line.Append(token);
        }

        if (line.Length > 0)
        {
            sb.Append(line);
        }
        return sb.ToString();
    }

    private static string EncodeByte(byte b)
    {
        // Printable ASCII except '=' passes through.
        if ((b >= 33 && b <= 60) || (b >= 62 && b <= 126))
        {
            return ((char)b).ToString();
        }
        // Space and tab pass through except at end of line (handled separately).
        if (b == ' ' || b == '\t')
        {
            return ((char)b).ToString();
        }
        return "=" + b.ToString("X2", CultureInfo.InvariantCulture);
    }

    private static void EscapeTrailingWhitespace(StringBuilder line)
    {
        if (line.Length == 0)
        {
            return;
        }

        char last = line[line.Length - 1];
        if (last == ' ' || last == '\t')
        {
            line.Length--;
            line.Append('=');
            line.Append(((byte)last).ToString("X2", CultureInfo.InvariantCulture));
        }
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') ||
        (c >= 'A' && c <= 'F') ||
        (c >= 'a' && c <= 'f');

    private static int HexValue(char c)
    {
        if (c >= '0' && c <= '9')
        {
            return c - '0';
        }
        if (c >= 'A' && c <= 'F')
        {
            return c - 'A' + 10;
        }
        return c - 'a' + 10;
    }
}
