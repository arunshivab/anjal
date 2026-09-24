using System.Text;
using System.Text.RegularExpressions;

namespace Anjal.Webmail.Services;

/// <summary>
/// Conversions between the formatted editor's HTML and plain text. The plain
/// version of an HTML message is always derived here, from the sanitised
/// HTML, so the two parts of a message can never say different things.
/// </summary>
public static partial class HtmlText
{
    /// <summary>
    /// Plain text from HTML: paragraphs, line breaks and list items become
    /// line breaks ("• " for bullets), a quote's lines are prefixed "&gt; ",
    /// markup is removed and entities are decoded.
    /// </summary>
    /// <param name="html">Sanitised HTML.</param>
    public static string ToPlain(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        // Numbered lists keep their numbers in the plain part: in a hospital
        // the order often carries the meaning - dosage steps, procedures
        // (DEF-034). Bulleted lists become "• ".
        string s = OrderedListRegex().Replace(html, m =>
        {
            int n = 0;
            // The trailing newline stands in for the </ol> this match consumed,
            // so the next paragraph starts on its own line.
            return ListItemRegex().Replace(m.Groups[1].Value, _ => "\n" + (++n).ToString(System.Globalization.CultureInfo.InvariantCulture) + ". ") + "\n";
        });
        s = BlockTagRegex().Replace(s, "\n");
        s = LineBreakRegex().Replace(s, "\n");
        s = ListItemRegex().Replace(s, "\n• ");
        s = QuoteRegex().Replace(s, m =>
        {
            string inner = TagRegex().Replace(m.Groups[1].Value, string.Empty);
            inner = System.Net.WebUtility.HtmlDecode(inner).Trim('\n');
            return "\n" + string.Join("\n", inner.Split('\n').Select(l => "> " + l)) + "\n";
        });
        s = TagRegex().Replace(s, string.Empty);
        s = System.Net.WebUtility.HtmlDecode(s);
        s = ManyBlankLinesRegex().Replace(s.Replace("\r\n", "\n", StringComparison.Ordinal), "\n\n");
        return string.Join("\n", s.Split('\n').Select(l => l.TrimEnd())).Trim('\n');
    }

    /// <summary>
    /// HTML from plain text in which quoted lines start with "&gt;": those
    /// become a blockquote, everything else is escaped and keeps its lines.
    /// </summary>
    /// <param name="text">Plain text.</param>
    public static string FromQuotedPlain(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder();
        var quote = new List<string>();
        void Flush()
        {
            if (quote.Count > 0)
            {
                sb.Append("<blockquote>").Append(string.Join("<br>", quote)).Append("</blockquote>");
                quote.Clear();
            }
        }
        foreach (string raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.StartsWith('>'))
            {
                quote.Add(System.Net.WebUtility.HtmlEncode(raw.Length > 1 && raw[1] == ' ' ? raw.Substring(2) : raw.Substring(1)));
                continue;
            }
            Flush();
            sb.Append("<div>").Append(raw.Length == 0 ? "<br>" : System.Net.WebUtility.HtmlEncode(raw)).Append("</div>");
        }
        Flush();
        return sb.ToString();
    }

    [GeneratedRegex(@"</(p|div|h[1-6]|ul|ol|tr|table)\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockTagRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LineBreakRegex();

    [GeneratedRegex(@"<ol[^>]*>(.*?)</ol\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex OrderedListRegex();

    [GeneratedRegex(@"<li[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"<blockquote[^>]*>(.*?)</blockquote\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex QuoteRegex();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex ManyBlankLinesRegex();
}
