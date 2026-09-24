using System.Diagnostics;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

/// <summary>
/// SEC-R1: a crafted message body must not be able to peg a CPU core when
/// someone opens it. Anyone who can send mail could trigger this, so these
/// inputs are the attack itself, at sizes that used to take minutes.
/// </summary>
public class SanitizerDosTests
{
    [Theory]
    [InlineData(40_000)]
    [InlineData(400_000)]
    [InlineData(2_000_000)]
    public void AnUnclosedTagFollowedByAHugeWhitespaceRun_IsHandledQuickly(int spaces)
    {
        string attack = "<a" + new string(' ', spaces);
        var sw = Stopwatch.StartNew();
        string clean = HtmlSanitizer.Sanitize(attack);
        sw.Stop();

        // Linear, not merely cut off by the timeout: 2 million spaces in
        // well under a second on the non-backtracking engine.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"{spaces} spaces took {sw.Elapsed.TotalSeconds:F1}s");
        Assert.DoesNotContain("<a", clean, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<a" + " \t/" + "                    ")]
    [InlineData("<div class=\"x\"" + "                    ")]
    [InlineData("<img src=\"a\" alt=")]
    public void OtherUnclosedShapes_AreAlsoHandled(string attack)
    {
        string clean = HtmlSanitizer.Sanitize(attack + new string(' ', 60_000));
        Assert.DoesNotContain("<", clean, StringComparison.Ordinal);
    }

    [Fact]
    public void AVeryLongBody_IsShortened_AndTheCallerIsTold()
    {
        string huge = "<p>start</p>" + new string('x', HtmlSanitizer.MaxHtmlChars) + "<p>end</p>";
        string clean = HtmlSanitizer.Sanitize(huge, allowRemoteImages: false, out bool shortened);

        Assert.True(shortened);
        Assert.Contains("start", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("end", clean, StringComparison.Ordinal);
        Assert.False(HtmlSanitizer.Sanitize("<p>small</p>", allowRemoteImages: false, out bool small) is null);
        Assert.False(small);
    }

    [Fact]
    public void OrdinaryHtml_IsUnchangedByTheHardening()
    {
        string clean = HtmlSanitizer.Sanitize("<p>Dear <b>Dr Rao</b>,</p><ul><li>CBC</li></ul><a href=\"https://apulki.in\">link</a><script>alert(1)</script>");
        Assert.Equal("<p>Dear <b>Dr Rao</b>,</p><ul><li>CBC</li></ul><a href=\"https://apulki.in\" target=\"_blank\" rel=\"noopener noreferrer nofollow\">link</a>", clean);
    }

    [Fact]
    public void TheShortenedFlag_ReachesTheReader()
    {
        // The flag is only worth setting if the page shows it.
        string page = System.IO.File.ReadAllText(System.IO.Path.Combine(
            RepoRoot(), "src", "Anjal.Webmail", "Components", "Pages", "Message.razor"));
        Assert.Contains("view.BodyShortened", page, StringComparison.Ordinal);
        Assert.Contains("cut short", page, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        System.IO.DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
