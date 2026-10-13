using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Anjal.Webmail.Services;

/// <summary>
/// The one layout for Anjal's own mails (rc.16, design review [6m], owner 11 Oct 2026): invitations,
/// sign-in alerts, reset codes, storage warnings and the operator's summaries. A header band (brand
/// for invitations and notices, warning-soft for security, accent-soft for automatic replies), the
/// title, the text, a box for "Name: value" lines and codes, one 44px button for the first link, and
/// a footer naming who sent it. Built from the plain text, which always goes with it.
/// </summary>
public static partial class SystemMailLayout
{
    /// <summary>A notice or an invitation: the brand band.</summary>
    public const string Notice = "notice";

    /// <summary>A security mail: the warning band.</summary>
    public const string Security = "security";

    /// <summary>An automatic reply: the accent band.</summary>
    public const string Automatic = "automatic";

    /// <summary>The HTML part of a mail from Anjal.</summary>
    /// <param name="kind"><see cref="Notice"/>, <see cref="Security"/> or <see cref="Automatic"/>.</param>
    /// <param name="title">The title (the subject).</param>
    /// <param name="text">The plain text the mail carries.</param>
    /// <param name="organisation">The organisation it is sent for.</param>
    /// <param name="host">The host it is sent from.</param>
    /// <returns>A complete HTML document, styled inline (mail programs drop style sheets).</returns>
    public static string Html(string kind, string title, string text, string organisation, string host)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(organisation);
        ArgumentNullException.ThrowIfNull(host);
        (string band, string bandInk) = kind switch
        {
            Security => ("#F8EBC9", "#7D5407"),
            Automatic => ("#FAEDD1", "#7D5407"),
            _ => ("#0E4B4F", "#FFFFFF"),
        };
        var body = new StringBuilder();
        bool button = false;
        foreach (string block in Regex.Split(text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim(), @"\n\s*\n"))
        {
            string[] lines = block.Split('\n');
            if (lines.All(l => KeyValue().IsMatch(l)))
            {
                body.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" style=\"width:100%;margin:0 0 18px;background:#F2F6F5;border:1px solid #D9E3E1;border-radius:12px\">\n");
                foreach (string line in lines)
                {
                    Match m = KeyValue().Match(line);
                    body.Append("<tr><td style=\"padding:8px 14px;color:#45585A;font-size:13px;width:90px;vertical-align:top\">").Append(E(m.Groups[1].Value))
                        .Append("</td><td style=\"padding:8px 14px 8px 0;color:#12201F;font-size:14px\">").Append(E(m.Groups[2].Value.Trim())).Append("</td></tr>\n");
                }
                body.Append("</table>\n");
                continue;
            }
            if (lines.Length == 1 && lines[0].StartsWith("    ", StringComparison.Ordinal))
            {
                string value = lines[0].Trim();
                if (Url().IsMatch(value) && !button)
                {
                    button = true;
                    body.Append("<p style=\"margin:4px 0 22px\"><a href=\"").Append(E(value)).Append("\" style=\"display:inline-block;height:44px;line-height:44px;padding:0 22px;border-radius:12px;background:#0E4B4F;color:#FFFFFF;font-weight:700;font-size:15px;text-decoration:none\">")
                        .Append(E(ButtonWord(text))).Append("</a></p>\n")
                        .Append("<p style=\"margin:0 0 18px;font-size:12px;color:#45585A;word-break:break-all\">").Append(E(value)).Append("</p>\n");
                }
                else
                {
                    body.Append("<p style=\"margin:0 0 18px;padding:12px 16px;background:#F2F6F5;border:1px solid #D9E3E1;border-radius:12px;font:700 26px/1.2 ui-monospace,Menlo,Consolas,monospace;letter-spacing:0.12em;color:#12201F\">")
                        .Append(E(value)).Append("</p>\n");
                }
                continue;
            }
            body.Append("<p style=\"margin:0 0 14px;font-size:15px;line-height:1.55;color:#12201F\">");
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    body.Append("<br>");
                }
                body.Append(Linked(lines[i].TrimEnd()));
            }
            body.Append("</p>\n");
        }
        string footer = string.Format(CultureInfo.InvariantCulture, "Sent by Anjal for {0} · {1}", organisation, host);
        return "<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>" + E(title) + "</title></head>\n"
            + "<body style=\"margin:0;padding:24px 12px;background:#F2F6F5;font-family:'LiPi Sans',system-ui,-apple-system,'Segoe UI',sans-serif\">\n"
            + "<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" style=\"width:100%;max-width:560px;margin:0 auto;background:#FFFFFF;border:1px solid #D9E3E1;border-radius:16px;overflow:hidden\">\n"
            + "<tr><td style=\"background:" + band + ";color:" + bandInk + ";padding:16px 24px;font-size:14px;font-weight:700;letter-spacing:0.01em\">" + E(organisation) + "</td></tr>\n"
            + "<tr><td style=\"padding:24px 24px 8px\">\n<h1 style=\"margin:0 0 16px;font-size:21px;font-weight:700;line-height:1.3;color:#12201F\">" + E(title) + "</h1>\n"
            + body
            + "</td></tr>\n<tr><td style=\"padding:14px 24px;border-top:1px solid #D9E3E1;font-size:12px;color:#45585A\">" + E(footer) + "</td></tr>\n</table>\n</body></html>\n";
    }

    // The button's word, from what the mail is for.
    private static string ButtonWord(string text) =>
        text.Contains("invited you", StringComparison.Ordinal) ? "Choose your password"
        : text.Contains("sign-in", StringComparison.OrdinalIgnoreCase) || text.Contains("sign in", StringComparison.OrdinalIgnoreCase) ? "Open the sign-in page"
        : "Open";

    // Plain text with its web addresses made into links.
    private static string Linked(string line)
    {
        var sb = new StringBuilder();
        int at = 0;
        foreach (Match m in Url().Matches(line))
        {
            sb.Append(E(line[at..m.Index]));
            sb.Append("<a href=\"").Append(E(m.Value)).Append("\" style=\"color:#0E4B4F\">").Append(E(m.Value)).Append("</a>");
            at = m.Index + m.Length;
        }
        sb.Append(E(line[at..]));
        return sb.ToString();
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);

    [GeneratedRegex(@"^([A-Z][A-Za-z ]{1,18}): (\S.*)$")]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"https?://[^\s<>""]+")]
    private static partial Regex Url();
}
