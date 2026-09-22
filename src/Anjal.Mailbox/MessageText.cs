namespace Anjal.Mailbox;

/// <summary>
/// The readable text of a message, for search: the first plain-text part,
/// or failing that the first HTML part with its markup removed. Attachments
/// are never included. Capped at <see cref="MaxChars"/> so one enormous
/// message cannot bloat the index.
/// </summary>
public static class MessageText
{
    /// <summary>The most characters kept per message.</summary>
    public const int MaxChars = 64 * 1024;

    /// <summary>Extract searchable text; empty when there is none.</summary>
    /// <param name="message">The parsed message, or null when it could not be parsed.</param>
    public static string Extract(Anjal.Mime.MimeMessage? message)
    {
        if (message?.Body is null)
        {
            return string.Empty;
        }
        Anjal.Mime.MimePart? plain = null;
        Anjal.Mime.MimePart? html = null;
        Find(message.Body, ref plain, ref html);
        string text = plain is not null ? Decode(plain)
            : html is not null ? StripHtml(Decode(html))
            : string.Empty;
        text = CollapseWhitespace(text);
        return text.Length <= MaxChars ? text : text.Substring(0, MaxChars);
    }

    private static void Find(Anjal.Mime.MimeEntity entity, ref Anjal.Mime.MimePart? plain, ref Anjal.Mime.MimePart? html)
    {
        if (entity is Anjal.Mime.MimeMultipart multi)
        {
            foreach (Anjal.Mime.MimeEntity child in multi.Parts)
            {
                Find(child, ref plain, ref html);
            }
            return;
        }
        if (entity is not Anjal.Mime.MimePart part)
        {
            return;
        }
        string disposition = part.Headers.Get("Content-Disposition") ?? string.Empty;
        if (disposition.StartsWith("attachment", System.StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        string mime = part.ContentType.MimeType;
        if (plain is null && mime == "text/plain")
        {
            plain = part;
        }
        else if (html is null && mime == "text/html")
        {
            html = part;
        }
    }

    /// <summary>
    /// Decode with the declared charset; with none declared, UTF-8, which
    /// reads plain ASCII identically and does not mangle Indian scripts sent
    /// without a charset parameter.
    /// </summary>
    private static string Decode(Anjal.Mime.MimePart part)
    {
        if (string.IsNullOrEmpty(part.ContentType.Charset))
        {
            return System.Text.Encoding.UTF8.GetString(part.Body);
        }
        return part.GetBodyAsText();
    }

    private static string StripHtml(string html)
    {
        string withoutBlocks = System.Text.RegularExpressions.Regex.Replace(
            html, "<(script|style)[^>]*>.*?</\\1\\s*>", " ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            System.TimeSpan.FromSeconds(1));
        string withoutTags = System.Text.RegularExpressions.Regex.Replace(
            withoutBlocks, "<[^>]*>", " ",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant, System.TimeSpan.FromSeconds(1));
        return System.Net.WebUtility.HtmlDecode(withoutTags);
    }

    private static string CollapseWhitespace(string text)
    {
        var sb = new System.Text.StringBuilder(System.Math.Min(text.Length, MaxChars + 16));
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space)
            {
                sb.Append(' ');
                space = false;
            }
            sb.Append(c);
            if (sb.Length > MaxChars)
            {
                break;
            }
        }
        return sb.ToString();
    }
}
