
namespace Anjal.Mime;

/// <summary>
/// Hand-rolled Base64 encoder and decoder per RFC 4648. Used for Content-
/// Transfer-Encoding base64 (RFC 2045 section 6.8). Tolerates whitespace and
/// line breaks in input as required by RFC 2045.
/// </summary>
public static class Base64Codec
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    private const int LineLength = 76;

    /// <summary>
    /// Decode a Base64-encoded string. Whitespace, CR, LF, and tab characters
    /// are silently skipped. Characters outside the Base64 alphabet (other
    /// than '=' padding and whitespace) throw <see cref="FormatException"/>.
    /// </summary>
    /// <param name="encoded">The Base64 input.</param>
    /// <returns>The decoded bytes.</returns>
    /// <exception cref="FormatException">If a non-Base64, non-whitespace
    /// character is encountered, or padding is malformed.</exception>
    public static byte[] Decode(string encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);

        var output = new List<byte>(encoded.Length * 3 / 4);
        int buffer = 0;
        int bitsCollected = 0;
        int padding = 0;

        foreach (char c in encoded)
        {
            if (c is ' ' or '\t' or '\r' or '\n')
            {
                continue;
            }

            if (c == '=')
            {
                padding++;
                continue;
            }

            if (padding > 0)
            {
                throw new FormatException("Non-padding character after '=' in Base64 input.");
            }

            int value = AlphabetIndex(c);
            if (value < 0)
            {
                throw new FormatException($"Invalid Base64 character '{c}'.");
            }

            buffer = (buffer << 6) | value;
            bitsCollected += 6;

            if (bitsCollected >= 8)
            {
                bitsCollected -= 8;
                output.Add((byte)((buffer >> bitsCollected) & 0xFF));
            }
        }

        return [.. output];
    }

    /// <summary>
    /// Encode a byte array as Base64, breaking the output into lines of
    /// 76 characters separated by CRLF per RFC 2045 section 6.8.
    /// </summary>
    /// <param name="data">The bytes to encode.</param>
    /// <returns>The Base64-encoded string with CRLF line breaks.</returns>
    public static string Encode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length == 0)
        {
            return string.Empty;
        }

        int outputCharCount = (data.Length + 2) / 3 * 4;
        var chars = new char[outputCharCount];
        int outIndex = 0;

        int fullGroups = data.Length / 3;
        for (int g = 0; g < fullGroups; g++)
        {
            int i = g * 3;
            int triple = (data[i] << 16) | (data[i + 1] << 8) | data[i + 2];
            chars[outIndex++] = Alphabet[(triple >> 18) & 0x3F];
            chars[outIndex++] = Alphabet[(triple >> 12) & 0x3F];
            chars[outIndex++] = Alphabet[(triple >> 6) & 0x3F];
            chars[outIndex++] = Alphabet[triple & 0x3F];
        }

        int remaining = data.Length - (fullGroups * 3);
        if (remaining == 1)
        {
            int triple = data[fullGroups * 3] << 16;
            chars[outIndex++] = Alphabet[(triple >> 18) & 0x3F];
            chars[outIndex++] = Alphabet[(triple >> 12) & 0x3F];
            chars[outIndex++] = '=';
            chars[outIndex++] = '=';
        }
        else if (remaining == 2)
        {
            int triple = (data[fullGroups * 3] << 16) | (data[(fullGroups * 3) + 1] << 8);
            chars[outIndex++] = Alphabet[(triple >> 18) & 0x3F];
            chars[outIndex++] = Alphabet[(triple >> 12) & 0x3F];
            chars[outIndex++] = Alphabet[(triple >> 6) & 0x3F];
            chars[outIndex++] = '=';
        }

        // Insert CRLF every 76 characters.
        var sb = new System.Text.StringBuilder(outputCharCount + ((outputCharCount / LineLength) * 2));
        for (int i = 0; i < outputCharCount; i += LineLength)
        {
            int chunk = Math.Min(LineLength, outputCharCount - i);
            sb.Append(chars, i, chunk);
            if (i + chunk < outputCharCount)
            {
                sb.Append("\r\n");
            }
        }
        return sb.ToString();
    }

    private static int AlphabetIndex(char c)
    {
        if (c >= 'A' && c <= 'Z')
        {
            return c - 'A';
        }
        if (c >= 'a' && c <= 'z')
        {
            return c - 'a' + 26;
        }
        if (c >= '0' && c <= '9')
        {
            return c - '0' + 52;
        }
        if (c == '+')
        {
            return 62;
        }
        if (c == '/')
        {
            return 63;
        }
        return -1;
    }
}
