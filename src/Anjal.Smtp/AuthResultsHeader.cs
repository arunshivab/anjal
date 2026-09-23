namespace Anjal.Smtp;

/// <summary>
/// Authentication-Results headers (RFC 8601). A sender can write one into a
/// message claiming anything - "dmarc=pass" included. So on arrival every
/// such header that names this server is removed, as RFC 8601 section 5
/// asks, and the one this server then adds is the only one bearing its name.
/// Readers trust only that one (DEF-038).
/// </summary>
public static class AuthResultsHeader
{
    private const string Name = "Authentication-Results";

    /// <summary>
    /// The authserv-id of a header value: the first token, before a ';' or
    /// whitespace (an optional version number may follow it).
    /// </summary>
    /// <param name="value">The field value.</param>
    public static string AuthServIdOf(string value)
    {
        System.ArgumentNullException.ThrowIfNull(value);
        string v = value.TrimStart();
        int end = 0;
        while (end < v.Length && v[end] != ';' && !char.IsWhiteSpace(v[end]))
        {
            end++;
        }
        return v.Substring(0, end);
    }

    /// <summary>
    /// Remove every Authentication-Results field in the header section whose
    /// authserv-id is <paramref name="authServId"/>. The body and all other
    /// fields are left exactly as they were.
    /// </summary>
    /// <param name="raw">The message.</param>
    /// <param name="authServId">This server's name.</param>
    public static byte[] RemoveClaimsBy(byte[] raw, string authServId)
    {
        System.ArgumentNullException.ThrowIfNull(raw);
        System.ArgumentNullException.ThrowIfNull(authServId);
        int headerEnd = HeaderSectionEnd(raw);
        var kept = new System.IO.MemoryStream(raw.Length);
        bool removedAny = false;
        int i = 0;
        while (i < headerEnd)
        {
            int fieldEnd = FieldEnd(raw, i, headerEnd);
            string field = System.Text.Encoding.Latin1.GetString(raw, i, fieldEnd - i);
            int colon = field.IndexOf(':', System.StringComparison.Ordinal);
            bool drop = colon > 0
                && string.Equals(field.Substring(0, colon).Trim(), Name, System.StringComparison.OrdinalIgnoreCase)
                && string.Equals(AuthServIdOf(field.Substring(colon + 1).Replace("\r\n", " ", System.StringComparison.Ordinal)), authServId, System.StringComparison.OrdinalIgnoreCase);
            if (drop)
            {
                removedAny = true;
            }
            else
            {
                kept.Write(raw, i, fieldEnd - i);
            }
            i = fieldEnd;
        }
        if (!removedAny)
        {
            return raw;
        }
        kept.Write(raw, headerEnd, raw.Length - headerEnd);
        return kept.ToArray();
    }

    /// <summary>Offset of the blank line ending the header section (or the end).</summary>
    private static int HeaderSectionEnd(byte[] raw)
    {
        for (int i = 0; i + 1 < raw.Length; i++)
        {
            if (raw[i] == '\r' && raw[i + 1] == '\n' && (i == 0 || (i >= 2 && raw[i - 2] == '\r' && raw[i - 1] == '\n')))
            {
                return i;
            }
        }
        return raw.Length;
    }

    /// <summary>End of the field starting at <paramref name="start"/>, including folded lines.</summary>
    private static int FieldEnd(byte[] raw, int start, int limit)
    {
        for (int i = start; i + 1 < limit; i++)
        {
            if (raw[i] == '\r' && raw[i + 1] == '\n')
            {
                int next = i + 2;
                if (next >= limit || (raw[next] != ' ' && raw[next] != '\t'))
                {
                    return next;
                }
            }
        }
        return limit;
    }
}
