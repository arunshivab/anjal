using System.Text.Json;

namespace Anjal.Webmail.Tests;

/// <summary>
/// UX-09 and SPEC-11 item 40: "contrast checked in all 24 themes" - kept true (26 since rc.15 added the house theme, Anjal)
/// by this test, which reads the design system's colours in the repository.
/// The same pairs as tools/check_contrast.py. The open item (D-112) is dark ink on
/// the tint (text, 4.5:1) with the theme colour as its icon (3:1, the rule for icons).
/// </summary>
public class Rc11ContrastTests
{
    private static readonly (string Fg, string Bg, double Min)[] Pairs =
    {
        ("ink", "surface-000", 4.5), ("ink", "surface-100", 4.5), ("ink", "surface-200", 4.5), ("ink", "surface-300", 4.5),
        ("ink-muted", "surface-100", 4.5), ("ink-muted", "surface-200", 4.5), ("on-brand", "brand", 4.5), ("brand", "surface-200", 4.5),
        ("brand", "brand-soft", 3.0), ("ink", "brand-soft", 4.5), ("ink", "warning-soft", 4.5), ("ink", "danger-soft", 4.5),
        ("danger", "surface-200", 4.5), ("on-seal", "seal", 4.5), ("seal", "surface-200", 3.0), ("focus-ring", "surface-200", 3.0),
        ("focus-ring", "surface-100", 3.0), ("line-strong", "surface-200", 3.0),
    };

    [Fact]
    public void EveryTheme_MeetsTheContrastMinimums()
    {
        using JsonDocument doc = JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "tools", "design", "tokens.json")));
        JsonElement color = doc.RootElement.GetProperty("color");
        var values = color.GetProperty("tokens").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("value"));
        var failures = new List<string>();
        int themes = 0;
        foreach (JsonElement theme in color.GetProperty("themes").EnumerateArray())
        {
            themes++;
            string id = theme.GetProperty("id").GetString()!;
            foreach ((string fg, string bg, double min) in Pairs)
            {
                double r = Ratio(values[fg].GetProperty(id).GetString()!, values[bg].GetProperty(id).GetString()!);
                if (r < min)
                {
                    failures.Add($"{id}: {fg} on {bg} is {r:0.00}:1, needs {min}");
                }
            }
        }
        Assert.Equal(42, themes); // DES-11 D4 (owner, 10 Oct 2026): 21 colours, each light and dark
        Assert.Empty(failures);
    }

    private static double Ratio(string a, string b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        string h = hex.Trim().TrimStart('#');
        double C(int i)
        {
            double c = Convert.ToInt32(h.Substring(i, 2), 16) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return (0.2126 * C(0)) + (0.7152 * C(2)) + (0.0722 * C(4));
    }

    private static string RepoRoot()
    {
        System.IO.DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
