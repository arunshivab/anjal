using System.Text;

namespace Anjal.Mime;

/// <summary>
/// Parses RFC 5322 address-list header values (e.g. From, To, Cc, Bcc).
/// Handles display names (both bare and quoted), angle-addresses, comments,
/// and RFC 2047 encoded-word display names. Pragmatic: accepts most real-world
/// headers, rejects only obviously malformed input.
/// </summary>
public static class AddressParser
{
    /// <summary>
    /// Parse a header value as a comma-separated list of mail addresses.
    /// Returns an empty list on null or empty input. Invalid entries are
    /// skipped silently.
    /// </summary>
    /// <param name="headerValue">The unfolded header value.</param>
    /// <returns>The parsed addresses, in order.</returns>
    public static IReadOnlyList<MailAddress> Parse(string? headerValue)
    {
        var results = new List<MailAddress>();
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return results;
        }

        foreach (string entry in SplitOnUnquotedComma(headerValue))
        {
            string trimmed = entry.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }
            if (TryParseOne(trimmed, out MailAddress? address) && address is not null)
            {
                results.Add(address);
            }
        }

        return results;
    }

    /// <summary>
    /// Try to parse a single mail address. Returns <see langword="false"/> if
    /// the input cannot be parsed.
    /// </summary>
    /// <param name="input">A single address.</param>
    /// <param name="address">The parsed address on success, otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if parsed.</returns>
    public static bool TryParseOne(string input, out MailAddress? address)
    {
        ArgumentNullException.ThrowIfNull(input);
        address = null;

        // Strip comments per RFC 5322 section 3.2.2.
        string cleaned = StripComments(input).Trim();
        if (cleaned.Length == 0)
        {
            return false;
        }

        // Two forms: "DisplayName <addr@host>" or just "addr@host".
        int lt = cleaned.LastIndexOf('<');
        int gt = cleaned.LastIndexOf('>');
        string addrSpec;
        string displayName = string.Empty;

        if (lt >= 0 && gt > lt)
        {
            addrSpec = cleaned.Substring(lt + 1, gt - lt - 1).Trim();
            string dn = cleaned.Substring(0, lt).Trim();
            displayName = UnquoteDisplayName(dn);
            displayName = EncodedWordDecoder.Decode(displayName);
        }
        else
        {
            addrSpec = cleaned;
        }

        if (!addrSpec.Contains('@', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            address = new MailAddress(addrSpec, string.IsNullOrEmpty(displayName) ? null : displayName);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static IEnumerable<string> SplitOnUnquotedComma(string s)
    {
        var sb = new StringBuilder();
        bool inQuote = false;
        int angle = 0;

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"' && (i == 0 || s[i - 1] != '\\'))
            {
                inQuote = !inQuote;
                sb.Append(c);
                continue;
            }
            if (!inQuote)
            {
                if (c == '<')
                {
                    angle++;
                }
                else if (c == '>')
                {
                    if (angle > 0)
                    {
                        angle--;
                    }
                }
                else if (c == ',' && angle == 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                    continue;
                }
            }
            sb.Append(c);
        }
        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }

    private static string StripComments(string s)
    {
        if (!s.Contains('(', StringComparison.Ordinal))
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        bool inQuote = false;
        int depth = 0;

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"' && (i == 0 || s[i - 1] != '\\'))
            {
                inQuote = !inQuote;
                if (depth == 0)
                {
                    sb.Append(c);
                }
                continue;
            }
            if (!inQuote)
            {
                if (c == '(')
                {
                    depth++;
                    continue;
                }
                if (c == ')')
                {
                    if (depth > 0)
                    {
                        depth--;
                    }
                    continue;
                }
            }
            if (depth == 0)
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static string UnquoteDisplayName(string dn)
    {
        string t = dn.Trim();
        if (t.Length >= 2 && t[0] == '"' && t[t.Length - 1] == '"')
        {
            var sb = new StringBuilder(t.Length);
            for (int i = 1; i < t.Length - 1; i++)
            {
                if (t[i] == '\\' && i + 1 < t.Length - 1)
                {
                    sb.Append(t[i + 1]);
                    i++;
                }
                else
                {
                    sb.Append(t[i]);
                }
            }
            return sb.ToString();
        }
        return t;
    }
}
