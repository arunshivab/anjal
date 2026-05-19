using System.Text;

namespace Anjal.Mime;

/// <summary>
/// Decodes RFC 2047 encoded-word tokens of the form
/// <c>=?charset?encoding?encoded-text?=</c> within header values.
/// Used so non-ASCII text in headers (Subject, From, To) round-trips correctly.
/// </summary>
public static class EncodedWordDecoder
{
    /// <summary>
    /// Decode all encoded-word tokens within a header value. Tokens that
    /// cannot be decoded are left as-is in the output. Per RFC 2047 section 6.2,
    /// whitespace between two adjacent encoded words is removed.
    /// </summary>
    /// <param name="value">The header value, possibly containing encoded words.</param>
    /// <returns>The decoded string.</returns>
    public static string Decode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!value.Contains("=?", StringComparison.Ordinal))
        {
            return value;
        }

        var output = new StringBuilder(value.Length);
        int i = 0;
        bool lastWasEncodedWord = false;

        while (i < value.Length)
        {
            if (i + 1 < value.Length && value[i] == '=' && value[i + 1] == '?')
            {
                int wordEnd = FindEncodedWordEnd(value, i);
                if (wordEnd > 0)
                {
                    string token = value.Substring(i, wordEnd - i);
                    if (TryDecodeWord(token, out string decoded))
                    {
                        if (lastWasEncodedWord)
                        {
                            // RFC 2047 section 6.2: remove whitespace between two
                            // adjacent encoded words.
                            while (output.Length > 0 && IsWhitespace(output[output.Length - 1]))
                            {
                                output.Length--;
                            }
                        }
                        output.Append(decoded);
                        i = wordEnd;
                        lastWasEncodedWord = true;
                        continue;
                    }
                }
            }

            output.Append(value[i]);
            if (!IsWhitespace(value[i]))
            {
                lastWasEncodedWord = false;
            }
            i++;
        }

        return output.ToString();
    }

    private static int FindEncodedWordEnd(string value, int start)
    {
        // Encoded word: =? charset ? encoding ? encoded-text ?=
        // Find the closing "?=" after three '?' separators.
        int q1 = value.IndexOf('?', start + 2);
        if (q1 < 0)
        {
            return -1;
        }
        int q2 = value.IndexOf('?', q1 + 1);
        if (q2 < 0)
        {
            return -1;
        }
        int q3 = value.IndexOf("?=", q2 + 1, StringComparison.Ordinal);
        if (q3 < 0)
        {
            return -1;
        }
        return q3 + 2;
    }

    private static bool TryDecodeWord(string token, out string decoded)
    {
        decoded = string.Empty;
        if (token.Length < 8 || !token.StartsWith("=?", StringComparison.Ordinal) || !token.EndsWith("?=", StringComparison.Ordinal))
        {
            return false;
        }

        string inner = token.Substring(2, token.Length - 4);
        int q1 = inner.IndexOf('?');
        if (q1 < 0)
        {
            return false;
        }
        int q2 = inner.IndexOf('?', q1 + 1);
        if (q2 < 0)
        {
            return false;
        }

        string charsetName = inner.Substring(0, q1);
        string encoding = inner.Substring(q1 + 1, q2 - q1 - 1);
        string text = inner.Substring(q2 + 1);

        byte[] bytes;
        try
        {
            if (encoding.Equals("B", StringComparison.OrdinalIgnoreCase))
            {
                bytes = Base64Codec.Decode(text);
            }
            else if (encoding.Equals("Q", StringComparison.OrdinalIgnoreCase))
            {
                // RFC 2047 section 4.2 rule (2): '_' represents 0x20.
                bytes = QuotedPrintableCodec.Decode(text.Replace('_', ' '));
            }
            else
            {
                return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }

        Encoding enc;
        try
        {
            enc = Encoding.GetEncoding(charsetName);
        }
        catch (ArgumentException)
        {
            // Fall back to UTF-8 if the named charset isn't known to this runtime.
            enc = Encoding.UTF8;
        }

        decoded = enc.GetString(bytes);
        return true;
    }

    private static bool IsWhitespace(char c) => c is ' ' or '\t';
}
