using System.Text.RegularExpressions;

namespace Anjal.Webmail.Tests;

/// <summary>
/// Owner, 10 Oct 2026 ("please check all scroll bars"): with real scroll bars drawn, a table kept
/// for screen readers made the Anjal console page scroll - a table grows to its rows, whatever
/// size "sr-only" asks. Such a table sits inside a box that can be made one pixel.
/// </summary>
public sealed partial class ScrollBarTests
{
    [Fact]
    public void NoTable_IsHiddenForScreenReadersOnItsOwn()
    {
        string root = RepoRoot();
        var found = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src", "Anjal.Webmail", "Components"), "*.razor", SearchOption.AllDirectories))
        {
            if (HiddenTable().IsMatch(File.ReadAllText(file)))
            {
                found.Add(Path.GetFileName(file));
            }
        }
        Assert.True(found.Count == 0, "Tables hidden with sr-only on their own: " + string.Join(", ", found));
    }

    [Fact]
    public void ScreenReaderBoxes_ArePinned_SoTheyNeverGrowThePage()
    {
        string css = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anjal.Webmail", "wwwroot", "app.css"));
        Assert.Contains(".sr-only { top: 0; left: 0; }", css, StringComparison.Ordinal);
    }

    [GeneratedRegex("<table[^>]*class=\"[^\"]*\\bsr-only\\b")]
    private static partial Regex HiddenTable();

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
