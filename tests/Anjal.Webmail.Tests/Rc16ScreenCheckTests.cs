using System.Text.RegularExpressions;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

/// <summary>
/// rc.16 screen check (12 Oct 2026): a custom dashboard range from the person's own days reached
/// PostgreSQL with the zone's offset (+05:30), which timestamptz refuses; /dashboard/safety answered 500.
/// </summary>
public sealed partial class Rc16ScreenCheckTests
{
    [Fact]
    public void CustomRange_IsMidnightInThePersonsZone_GivenAsUtc()
    {
        ZonedClock india = ZonedClock.For("Asia/Kolkata", null);
        DateTimeOffset now = new(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);
        DashPeriod p = MailboxService.DashPagePeriodOf("from:2026-09-12:to:2026-10-11", india, now);
        Assert.Equal("Custom", p.Label);
        Assert.Equal(TimeSpan.Zero, p.Start.Offset);
        Assert.Equal(TimeSpan.Zero, p.End.Offset);
        Assert.Equal(new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.FromMinutes(330)), p.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.FromMinutes(330)), p.End);
    }

    [Fact]
    public void AtLocal_IsTheSameInstant_InUtc()
    {
        ZonedClock india = ZonedClock.For("Asia/Kolkata", null);
        DateTimeOffset at = MailboxService.AtLocal(india, new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Unspecified));
        Assert.Equal(TimeSpan.Zero, at.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 3, 30, 0, TimeSpan.Zero), at);
    }

    [Fact]
    public void NoAttributeWithAValue_WritesAnAmpersandAsEntity()
    {
        // 12 Oct 2026: in a .razor attribute that mixes text and @values, the text is HTML-encoded
        // when the page is drawn, so "&amp;" became "&amp;amp;" and the link lost its second value
        // (Contacts' figures searched for nothing; Lajna's suggestions lost their page). Write "&".
        var found = new List<string>();
        foreach (string razor in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Anjal.Webmail", "Components"), "*.razor", SearchOption.AllDirectories))
        {
            foreach (Match m in EntityInValue().Matches(File.ReadAllText(razor)))
            {
                if (m.Value.Contains('@', StringComparison.Ordinal))
                {
                    found.Add(Path.GetFileName(razor) + ": " + m.Value[..Math.Min(80, m.Value.Length)]);
                }
            }
        }
        Assert.True(found.Count == 0, "Attributes with values that write &amp;: " + string.Join(" | ", found));
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex("=\"[^\"]*&amp;[^\"]*\"")]
    private static partial Regex EntityInValue();
}
