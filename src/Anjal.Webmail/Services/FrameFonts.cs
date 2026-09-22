using System.Text.RegularExpressions;

namespace Anjal.Webmail.Services;

/// <summary>
/// LiPi Sans for the sandboxed message frame (DEF-021). The frame cannot use
/// the page's stylesheet, so the @font-face rules from the embedded
/// fonts/lipi.css are copied into its own style block with absolute
/// addresses. The rules are read once; only the address differs per request.
/// </summary>
public static partial class FrameFonts
{
    private static readonly Lazy<string> Rules = new(() =>
    {
        byte[]? css = StaticAssets.Load("fonts/lipi.css");
        if (css is null)
        {
            return string.Empty;
        }
        string text = System.Text.Encoding.UTF8.GetString(css);
        var faces = new System.Text.StringBuilder();
        foreach (Match m in FontFaceRegex().Matches(text))
        {
            faces.Append(m.Value);
        }
        return faces.ToString();
    });

    /// <summary>
    /// The @font-face rules with each <c>url("/fonts/...")</c> made absolute
    /// against <paramref name="origin"/>.
    /// </summary>
    /// <param name="origin">This server's scheme and host, e.g. https://mail.anjal.co.in.</param>
    public static string FaceRules(string origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return Rules.Value.Replace("url(\"/fonts/", "url(\"" + origin + "/fonts/", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"@font-face\s*\{[^}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex FontFaceRegex();
}
