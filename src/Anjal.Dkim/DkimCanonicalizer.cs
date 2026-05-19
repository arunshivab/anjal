using System.Text;

namespace Anjal.Dkim;

/// <summary>
/// Implements RFC 6376 section 3.4 header and body canonicalization for both
/// <c>simple</c> and <c>relaxed</c> algorithms. The output of these methods is
/// the byte sequence that gets hashed (for the body, into <c>bh=</c>) or
/// signed (the header set plus the DKIM-Signature line with empty <c>b=</c>).
/// </summary>
public static class DkimCanonicalizer
{
    /// <summary>
    /// Canonicalize a single header value (everything after the colon, not
    /// including the colon itself or the trailing CRLF).
    /// </summary>
    /// <param name="name">Header name (e.g. "From").</param>
    /// <param name="value">Header value including any folded continuation lines.</param>
    /// <param name="canon">Algorithm.</param>
    /// <returns>The canonicalized "name: value\r\n" line.</returns>
    public static string CanonHeader(string name, string value, HeaderCanonicalization canon)
    {
        System.ArgumentNullException.ThrowIfNull(name);
        System.ArgumentNullException.ThrowIfNull(value);

        if (canon == HeaderCanonicalization.Simple)
        {
            // Verbatim: name + ":" + value, but a single CRLF terminator.
            // Strip any final CRLF from value (we add our own).
            string trimmed = StripFinalCrlf(value);
            return name + ":" + trimmed + "\r\n";
        }

        // Relaxed:
        //  1. Lowercase header name.
        //  2. Unfold continuation lines (remove CRLF that is followed by WSP).
        //  3. Collapse runs of WSP (space + tab) to a single SP.
        //  4. Strip WSP at end of header value (after step 2-3).
        //  5. Strip WSP around the colon separator (so the header has no
        //     leading WSP and the value has no leading WSP).
        string lower = name.ToLowerInvariant();
        string unfolded = UnfoldHeader(value);
        string collapsed = CollapseWhitespace(unfolded);
        string trimmedValue = collapsed.TrimEnd(' ', '\t');
        // Strip leading WSP from value (RFC 6376 section 3.4.2 step 5).
        trimmedValue = trimmedValue.TrimStart(' ', '\t');
        return lower + ":" + trimmedValue + "\r\n";
    }

    /// <summary>
    /// Canonicalize the message body.
    /// </summary>
    /// <param name="body">The full body bytes (everything after the empty
    /// line that separates headers from body, not including that empty line).</param>
    /// <param name="canon">Algorithm.</param>
    /// <returns>The canonicalized body bytes.</returns>
    public static byte[] CanonBody(byte[] body, BodyCanonicalization canon)
    {
        System.ArgumentNullException.ThrowIfNull(body);

        // Normalize line endings to CRLF. Some inputs may use bare LF.
        string text = Encoding.UTF8.GetString(body);
        text = text.Replace("\r\n", "\n", System.StringComparison.Ordinal).Replace("\n", "\r\n", System.StringComparison.Ordinal);

        if (canon == BodyCanonicalization.Simple)
        {
            // Section 3.4.3:
            //  - Ignore all empty lines at end of body.
            //  - If body has no body, output is "\r\n".
            //  - If body ends in any number of "\r\n" lines, reduce to a single "\r\n".
            string trimmed = TrimTrailingCrlfLines(text);
            if (trimmed.Length == 0)
            {
                return Encoding.UTF8.GetBytes("\r\n");
            }
            // Always append exactly one CRLF as terminator.
            if (!trimmed.EndsWith("\r\n", System.StringComparison.Ordinal))
            {
                trimmed += "\r\n";
            }
            return Encoding.UTF8.GetBytes(trimmed);
        }

        // Relaxed (section 3.4.4):
        //  - Ignore all whitespace at end of lines (before CRLF).
        //  - Reduce all sequences of WSP within a line to a single SP.
        //  - Ignore all empty lines at end of body.
        //  - If body is empty, output is empty string (NOT "\r\n").
        string[] lines = text.Split("\r\n");
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string collapsed = CollapseWhitespace(line);
            collapsed = collapsed.TrimEnd(' ', '\t');
            sb.Append(collapsed);
            // Don't append CRLF after the very last fragment (no trailing empty).
            if (i < lines.Length - 1)
            {
                sb.Append("\r\n");
            }
        }
        string result = TrimTrailingCrlfLines(sb.ToString());
        if (result.Length == 0)
        {
            return System.Array.Empty<byte>();
        }
        // Relaxed requires a final CRLF on the last non-empty line per RFC.
        if (!result.EndsWith("\r\n", System.StringComparison.Ordinal))
        {
            result += "\r\n";
        }
        return Encoding.UTF8.GetBytes(result);
    }

    /// <summary>
    /// Strip trailing CRLF-terminated empty lines from a body string. The
    /// non-empty content (if any) is preserved with its terminating CRLF.
    /// </summary>
    private static string TrimTrailingCrlfLines(string text)
    {
        if (text.Length == 0) return text;
        // Repeatedly strip trailing "\r\n" while the line before it is also empty.
        // Simpler: split, drop trailing empty entries, re-join.
        string[] parts = text.Split("\r\n");
        int lastNonEmpty = parts.Length - 1;
        while (lastNonEmpty >= 0 && parts[lastNonEmpty].Length == 0)
        {
            lastNonEmpty--;
        }
        if (lastNonEmpty < 0)
        {
            return string.Empty;
        }
        var sb = new StringBuilder();
        for (int i = 0; i <= lastNonEmpty; i++)
        {
            sb.Append(parts[i]);
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Unfold a header value: remove CRLF that is followed by WSP. Per RFC
    /// 5322 section 2.2.3, folding can occur anywhere FWS is allowed. The
    /// continuation WSP itself is preserved.
    /// </summary>
    private static string UnfoldHeader(string value)
    {
        // Replace "\r\n " or "\r\n\t" (the CRLF before continuation WSP) with just the WSP.
        // We do this by removing CRLF that is immediately followed by SP or HTAB.
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\r' && i + 2 < value.Length && value[i + 1] == '\n' &&
                (value[i + 2] == ' ' || value[i + 2] == '\t'))
            {
                // Skip the CRLF; the WSP becomes the join.
                i++;
                continue;
            }
            if (c == '\n' && i + 1 < value.Length && (value[i + 1] == ' ' || value[i + 1] == '\t'))
            {
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Collapse runs of SP and HTAB into a single SP. Other whitespace is left.
    /// </summary>
    private static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool inWsp = false;
        foreach (char c in s)
        {
            if (c == ' ' || c == '\t')
            {
                if (!inWsp)
                {
                    sb.Append(' ');
                    inWsp = true;
                }
            }
            else
            {
                sb.Append(c);
                inWsp = false;
            }
        }
        return sb.ToString();
    }

    private static string StripFinalCrlf(string s)
    {
        if (s.EndsWith("\r\n", System.StringComparison.Ordinal))
        {
            return s.Substring(0, s.Length - 2);
        }
        return s;
    }
}
