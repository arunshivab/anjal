namespace Anjal.Mime.Tests;

/// <summary>
/// v1.0.0-rc.8: dates Anjal writes are in the configured zone with their true
/// offset - the time and its label can never disagree.
/// </summary>
public sealed class Rc8MessageDateTests
{
    private static readonly System.DateTimeOffset RediffTest = new(2026, 9, 27, 17, 54, 33, System.TimeSpan.Zero);

    [Fact]
    public void TheRediffTestMessage_IsWrittenInIndiaTime()
    {
        Assert.Equal("Sun, 27 Sep 2026 23:24:33 +0530", MessageDate.Format(RediffTest, MessageDate.Resolve("Asia/Kolkata")));
    }

    [Fact]
    public void AnInstantGivenWithAnotherOffset_IsConverted_NotRelabelled()
    {
        var sameInstant = new System.DateTimeOffset(2026, 9, 27, 19, 54, 33, System.TimeSpan.FromHours(2));
        Assert.Equal("Sun, 27 Sep 2026 23:24:33 +0530", MessageDate.Format(sameInstant, MessageDate.Resolve("Asia/Kolkata")));
        Assert.Equal("Sun, 27 Sep 2026 17:54:33 +0000", MessageDate.Format(sameInstant, System.TimeZoneInfo.Utc));
    }

    [Fact]
    public void ANegativeOffset_IsWrittenWithAMinus()
    {
        Assert.Equal("Sun, 27 Sep 2026 13:54:33 -0400", MessageDate.Format(RediffTest, MessageDate.Resolve("America/New_York")));
    }

    [Fact]
    public void AnUnknownZone_FallsBackToUtc_AndTheDefaultIsIndia()
    {
        Assert.Equal(System.TimeZoneInfo.Utc, MessageDate.Resolve("Nowhere/Invalid"));
        Assert.Equal("Sun, 27 Sep 2026 23:24:33 +0530", MessageDate.Format(RediffTest, MessageDate.Resolve(null)));
    }
}
