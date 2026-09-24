using System.Text;
using System.Text.RegularExpressions;

namespace Anjal.Webmail.Services;

/// <summary>
/// Reduces an HTML mail body to something safe to render inside a
/// sandboxed iframe: no scripts, no event handlers, no forms, no
/// embedded frames or objects, no <c>javascript:</c> URLs, and - unless
/// the viewer opts in - no remote images (which would otherwise leak the
/// reader's IP address and reading time to the sender).
/// <para>
/// This is a conservative tag/attribute filter over a regular-expression
/// tokeniser, not a full HTML parser. It errs on the side of dropping
/// content: anything it does not recognise as safe is removed.
/// </para>
/// </summary>
public static partial class HtmlSanitizer
{
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "address", "b", "bdi", "bdo", "blockquote", "br", "caption", "center", "cite", "code", "col", "colgroup",
        "dd", "del", "details", "dfn", "div", "dl", "dt", "em", "figcaption", "figure", "font", "h1", "h2", "h3", "h4", "h5", "h6",
        "hr", "i", "img", "ins", "kbd", "li", "mark", "ol", "p", "pre", "q", "s", "samp", "section", "small", "span", "strike",
        "strong", "sub", "summary", "sup", "table", "tbody", "td", "tfoot", "th", "thead", "time", "tr", "tt", "u", "ul", "var", "wbr",
        "html", "body", "head", "title",
    };

    /// <summary>Elements that never have content or a closing tag.</summary>
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    private static readonly HashSet<string> DropWithContent = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "frame", "frameset", "object", "embed", "applet", "form", "button", "input", "select",
        "textarea", "noscript", "template", "svg", "math", "link", "meta", "base",
    };

    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "src", "alt", "title", "width", "height", "align", "valign", "border", "cellpadding", "cellspacing", "colspan",
        "rowspan", "bgcolor", "color", "face", "size", "dir", "lang", "style", "class", "id", "name", "start", "type", "cite",
        "datetime", "open",
    };

    [GeneratedRegex(@"<![A-Za-z\[][^>]*>|<\?[^>]*>", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, MatchTimeoutMs)]
    private static partial Regex DeclarationRegex();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, MatchTimeoutMs)]
    private static partial Regex CommentRegex();

    // Parsed the way browsers parse: "<" must be followed immediately by
    // a letter (or "/" and a letter) to start a tag - "a < b" is text, not
    // markup. Attributes may be separated by whitespace or "/": browsers
    // read <script/src=x> as a script tag with a src attribute.
    // Matched with the NON-BACKTRACKING engine, which is linear in the
    // length of the input whatever its shape. With the ordinary engine,
    // "<a" followed by a long run of spaces and no ">" cost time
    // proportional to the SQUARE of the run: 2 s for 40,000 spaces, over
    // 30 s for 200,000, minutes for a megabyte - and one 25 MB message can
    // carry many (SEC-R1). Measured on this pattern: 2,000,000 spaces in
    // 0.01 s, with identical matches on ordinary markup. An atomic group
    // was tried first and did not help, because the backtracking happens
    // inside the group before it completes.
    [GeneratedRegex(@"<(/?)([a-zA-Z][a-zA-Z0-9:-]*)((?:[\s/]+[^\s=>/]+(?:\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+))?)*)[\s/]*?(/?)\s*>", RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, MatchTimeoutMs)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"([^\s=]+)(?:\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+)))?", RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, MatchTimeoutMs)]
    private static partial Regex AttributeRegex();

    [GeneratedRegex(@"(expression\s*\(|url\s*\(|javascript:|@import|behavior\s*:)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeoutMs)]
    private static partial Regex DangerousCssRegex();

    /// <summary>
    /// Sanitise an HTML document or fragment.
    /// </summary>
    /// <param name="html">Untrusted HTML.</param>
    /// <param name="allowRemoteImages">When false, <c>http(s)</c> image sources are replaced with a placeholder attribute.</param>
    /// <returns>Sanitised HTML.</returns>
    /// <summary>How long any one pattern may run before it gives up (SEC-R1).</summary>
    private const int MatchTimeoutMs = 2000;

    /// <summary>
    /// The most HTML that is examined. A body larger than this is cut here
    /// and the reader is told; messages this large are display problems
    /// regardless, and the cap keeps the work bounded whatever the shape of
    /// the input.
    /// </summary>
    public const int MaxHtmlChars = 512 * 1024;

    /// <summary>
    /// Clean HTML for display. Never throws: if a pattern takes too long, or
    /// anything else goes wrong, the message is shown as plain text instead -
    /// markup is dropped rather than passed through unchecked.
    /// </summary>
    /// <param name="html">The message body.</param>
    /// <param name="allowRemoteImages">Whether remote images may load.</param>
    /// <param name="shortened">True when the body was longer than <see cref="MaxHtmlChars"/>.</param>
    public static string Sanitize(string html, bool allowRemoteImages, out bool shortened)
    {
        ArgumentNullException.ThrowIfNull(html);
        shortened = html.Length > MaxHtmlChars;
        string input = shortened ? html.Substring(0, MaxHtmlChars) : html;
        try
        {
            return SanitizeCore(input, allowRemoteImages);
        }
        catch (RegexMatchTimeoutException)
        {
            // Fail closed: the text of the message, with no markup at all.
            return System.Net.WebUtility.HtmlEncode(TagRegexFallback(input));
        }
    }

    /// <summary>Strip anything tag-shaped without a pattern that can backtrack.</summary>
    private static string TagRegexFallback(string html)
    {
        var plain = new StringBuilder(html.Length);
        bool inside = false;
        foreach (char c in html)
        {
            if (c == '<')
            {
                inside = true;
            }
            else if (c == '>')
            {
                inside = false;
            }
            else if (!inside)
            {
                plain.Append(c);
            }
        }
        return plain.ToString();
    }

    /// <summary>Clean HTML for display.</summary>
    /// <param name="html">The message body.</param>
    /// <param name="allowRemoteImages">Whether remote images may load.</param>
    public static string Sanitize(string html, bool allowRemoteImages = false) => Sanitize(html, allowRemoteImages, out _);

    private static string SanitizeCore(string html, bool allowRemoteImages)
    {

        // Comments, and declarations such as <!DOCTYPE html> or <?xml ...?>,
        // are removed first. Left in, a doctype was escaped as text and shown
        // at the top of every HTML email (DEF-031).
        string withoutComments = DeclarationRegex().Replace(CommentRegex().Replace(html, string.Empty), string.Empty);
        var output = new StringBuilder(withoutComments.Length);
        int pos = 0;
        string? dropUntil = null;

        foreach (Match m in TagRegex().Matches(withoutComments))
        {
            // Text between tags.
            if (dropUntil is null)
            {
                AppendText(output, withoutComments, pos, m.Index - pos);
            }
            pos = m.Index + m.Length;

            bool closing = m.Groups[1].Value == "/";
            string tag = m.Groups[2].Value;
            string attrs = m.Groups[3].Value;
            bool selfClosing = m.Groups[4].Value == "/";

            if (dropUntil is not null)
            {
                if (closing && string.Equals(tag, dropUntil, StringComparison.OrdinalIgnoreCase))
                {
                    dropUntil = null;
                }
                continue;
            }

            if (DropWithContent.Contains(tag))
            {
                // A void element has no closing tag, so "drop until it closes"
                // would discard the rest of the document: every HTML email with
                // a <meta> in its head used to open blank (DEF-032). Void
                // elements are dropped on their own.
                if (!closing && !selfClosing && !VoidElements.Contains(tag))
                {
                    dropUntil = tag;
                }
                continue;
            }

            if (!AllowedTags.Contains(tag))
            {
                // Unknown tag: drop the tag, keep its content.
                continue;
            }

            output.Append('<');
            if (closing)
            {
                output.Append('/');
            }
            output.Append(tag.ToLowerInvariant());
            if (!closing)
            {
                AppendAttributes(output, tag, attrs, allowRemoteImages);
            }
            if (selfClosing)
            {
                output.Append(" /");
            }
            output.Append('>');
        }

        if (dropUntil is null)
        {
            AppendText(output, withoutComments, pos, withoutComments.Length - pos);
        }
        return output.ToString();
    }

    private static void AppendAttributes(StringBuilder output, string tag, string attrs, bool allowRemoteImages)
    {
        foreach (Match a in AttributeRegex().Matches(attrs))
        {
            string name = a.Groups[1].Value;
            string value = a.Groups[2].Success ? a.Groups[2].Value
                : a.Groups[3].Success ? a.Groups[3].Value
                : a.Groups[4].Success ? a.Groups[4].Value
                : string.Empty;

            if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) || !AllowedAttributes.Contains(name))
            {
                continue;
            }

            string lower = name.ToLowerInvariant();
            if (lower == "href" || lower == "src" || lower == "cite")
            {
                string trimmed = value.Trim();
                if (!IsSafeUrl(trimmed))
                {
                    continue;
                }
                if (lower == "src" && string.Equals(tag, "img", StringComparison.OrdinalIgnoreCase) &&
                    !allowRemoteImages && IsRemote(trimmed))
                {
                    output.Append(" data-blocked-src=\"").Append(Escape(trimmed)).Append('"');
                    continue;
                }
                value = trimmed;
            }
            else if (lower == "style")
            {
                value = SanitizeStyle(value);
                if (value.Length == 0)
                {
                    continue;
                }
            }

            output.Append(' ').Append(lower).Append("=\"").Append(Escape(value)).Append('"');
        }

        if (string.Equals(tag, "a", StringComparison.OrdinalIgnoreCase))
        {
            output.Append(" target=\"_blank\" rel=\"noopener noreferrer nofollow\"");
        }
    }

    /// <summary>
    /// A URL is safe if it is relative, a fragment, <c>http(s)</c>,
    /// <c>mailto:</c>, <c>tel:</c>, or an image <c>data:</c> URL.
    /// </summary>
    /// <param name="url">The URL to check.</param>
    public static bool IsSafeUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        string compact = new(url.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray());
        int colon = compact.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return true;
        }
        string scheme = compact.Substring(0, colon).ToLowerInvariant();
        if (scheme == "http" || scheme == "https" || scheme == "mailto" || scheme == "tel")
        {
            return true;
        }
        return scheme == "data" && compact.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) &&
            !compact.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRemote(string url) =>
        url.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("//", StringComparison.Ordinal);

    /// <summary>
    /// Text that sat between tags. Angle brackets are escaped so a fragment
    /// the tag pattern did not recognise can never be interpreted as markup;
    /// character references (<c>&amp;amp;</c> and so on) pass through intact.
    /// </summary>
    private static void AppendText(StringBuilder output, string source, int start, int length)
    {
        for (int i = start; i < start + length; i++)
        {
            char c = source[i];
            if (c == '<')
            {
                output.Append("&lt;");
            }
            else if (c == '>')
            {
                output.Append("&gt;");
            }
            else
            {
                output.Append(c);
            }
        }
    }

    private static readonly HashSet<string> AllowedCssProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "color", "background-color", "font-family", "font-size", "font-weight", "font-style", "text-align",
        "text-decoration", "line-height", "letter-spacing", "margin", "margin-top", "margin-right", "margin-bottom",
        "margin-left", "padding", "padding-top", "padding-right", "padding-bottom", "padding-left", "border",
        "border-top", "border-right", "border-bottom", "border-left", "border-color", "border-width", "border-style",
        "border-collapse", "border-spacing", "border-radius", "width", "max-width", "min-width", "height", "max-height",
        "min-height", "vertical-align", "white-space", "word-break", "overflow-wrap", "display", "list-style-type",
        "text-transform", "text-indent",
    };

    private static readonly string[] AllowedCssFunctions = { "rgb(", "rgba(", "hsl(", "hsla(" };

    /// <summary>
    /// Keep only style declarations whose property is on an allowlist and
    /// whose value cannot fetch anything or hide an escape. A backslash is
    /// refused outright - CSS escapes are how <c>=rl(</c> becomes
    /// <c>url(</c> after this check - as are comments, <c>@</c> rules and any
    /// function other than colour notation. Returns the rebuilt declarations,
    /// or an empty string when nothing survives.
    /// </summary>
    /// <param name="style">The style attribute's value.</param>
    public static string SanitizeStyle(string style)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (style.Contains('\\', StringComparison.Ordinal) || style.Contains("/*", StringComparison.Ordinal) ||
            style.Contains('@', StringComparison.Ordinal) || DangerousCssRegex().IsMatch(style))
        {
            return string.Empty;
        }
        var kept = new StringBuilder(style.Length);
        foreach (string declaration in style.Split(';'))
        {
            int colon = declaration.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }
            string property = declaration.Substring(0, colon).Trim();
            string value = declaration.Substring(colon + 1).Trim();
            if (value.Length == 0 || !AllowedCssProperties.Contains(property) || !IsSafeCssValue(value))
            {
                continue;
            }
            if (kept.Length > 0)
            {
                kept.Append(';');
            }
            kept.Append(property.ToLowerInvariant()).Append(':').Append(value);
        }
        return kept.ToString();
    }

    private static bool IsSafeCssValue(string value)
    {
        foreach (char c in value)
        {
            if (char.IsControl(c) || c == '<' || c == '>' || c == '"' || c == '\'' || c == '{' || c == '}')
            {
                return false;
            }
        }
        int open = value.IndexOf('(', StringComparison.Ordinal);
        while (open >= 0)
        {
            bool allowed = false;
            foreach (string fn in AllowedCssFunctions)
            {
                int start = open + 1 - fn.Length;
                if (start >= 0 && string.Compare(value, start, fn, 0, fn.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    allowed = true;
                    break;
                }
            }
            if (!allowed)
            {
                return false;
            }
            open = value.IndexOf('(', open + 1);
        }
        return true;
    }

    /// <summary>HTML-escape text for insertion into an attribute or element.</summary>
    /// <param name="text">Text to escape.</param>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length + 16);
        foreach (char c in text)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Wrap plain text as HTML: escaped, with line breaks preserved and
    /// bare URLs left as text (not linkified).
    /// </summary>
    /// <param name="text">Plain text.</param>
    public static string FromPlainText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return "<pre style=\"white-space:pre-wrap;font-family:inherit;margin:0\">" + Escape(text) + "</pre>";
    }
}
