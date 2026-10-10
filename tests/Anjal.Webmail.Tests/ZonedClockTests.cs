using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

public class ZonedClockTests
{
    // "Now" is fixed in every test, so none depends on the day it runs (DEF-090).
    private static readonly System.DateTimeOffset Now = new(2026, 10, 2, 4, 30, 0, System.TimeSpan.Zero); // Fri 2 Oct 2026, 10:00 IST

    [Fact]
    public void AMessageAt2000UtcOnTheFirst_IsTodayInIndia_OnTheMorningOfTheSecond()
    {
        // DEF-088: "today" is decided in the person's zone, not the server's.
        var at = new System.DateTimeOffset(2026, 10, 1, 20, 0, 0, System.TimeSpan.Zero); // 2 Oct 01:30 IST
        Assert.Equal("01:30", ZonedClock.For("Asia/Kolkata", null).List(at, Now));
        Assert.Equal("Yesterday", ZonedClock.For("Europe/London", null).List(at, Now));
    }

    [Fact]
    public void TheList_UsesTodayYesterdayWeekdayDayMonthAndFullDate()
    {
        ZonedClock ist = ZonedClock.Default;
        Assert.Equal("09:42", ist.List(new System.DateTimeOffset(2026, 10, 2, 9, 42, 0, new System.TimeSpan(5, 30, 0)), Now));
        Assert.Equal("Yesterday", ist.List(new System.DateTimeOffset(2026, 10, 1, 18, 20, 0, new System.TimeSpan(5, 30, 0)), Now));
        Assert.Equal("Mon", ist.List(new System.DateTimeOffset(2026, 9, 28, 10, 0, 0, new System.TimeSpan(5, 30, 0)), Now));
        Assert.Equal("12 Sep", ist.List(new System.DateTimeOffset(2026, 9, 12, 10, 0, 0, new System.TimeSpan(5, 30, 0)), Now));
        Assert.Equal("30 Dec 2025", ist.List(new System.DateTimeOffset(2025, 12, 30, 10, 0, 0, new System.TimeSpan(5, 30, 0)), Now));
    }

    [Fact]
    public void TheFullForm_IsTheWeekdayDateTimeAndZone()
    {
        var at = new System.DateTimeOffset(2026, 10, 2, 4, 12, 31, System.TimeSpan.Zero);
        Assert.Equal("Fri 2 Oct 2026, 09:42 IST", ZonedClock.Default.Full(at));
        Assert.Equal("Fri 2 Oct 2026, 05:12 BST", ZonedClock.For("Europe/London", null).Full(at));
        Assert.Equal("Thu 1 Jan 2026, 04:12 GMT", ZonedClock.For("Europe/London", null).Full(new System.DateTimeOffset(2026, 1, 1, 4, 12, 0, System.TimeSpan.Zero)));
        Assert.Equal("Fri 2 Oct 2026, 08:12 GST", ZonedClock.For("Asia/Dubai", null).Full(at));
        Assert.Equal("Fri 2 Oct 2026, 13:12 UTC+09:00", ZonedClock.For("Asia/Tokyo", null).Full(at));
    }

    [Theory]
    [InlineData("language", "2 Oct 2026")]
    [InlineData("d-mmm-yyyy", "2 Oct 2026")]
    [InlineData("dd/mm/yyyy", "02/10/2026")]
    [InlineData("yyyy-mm-dd", "2026-10-02")]
    [InlineData("nonsense", "2 Oct 2026")]
    public void TheDate_FollowsTheChosenFormat(string format, string expected)
    {
        Assert.Equal(expected, ZonedClock.For("Asia/Kolkata", format).Date(Now));
    }

    [Fact]
    public void AnUnknownZone_FallsBackToIndia_SoAPageAlwaysShows()
    {
        Assert.Equal("Asia/Kolkata", ZonedClock.For("Mars/Olympus", null).ZoneId);
        Assert.Equal("Asia/Kolkata", ZonedClock.For(null, null).ZoneId);
    }

    [Fact]
    public void TheReplyLine_IsInThePersonsZone_WithTheZoneNamed()
    {
        // The sender wrote at 04:12 UTC; the person replying reads India time (DEF-088).
        Assert.Equal("On 2 October 2026 at 09:42 IST, Nandini wrote:", MailboxService.AttributionLine("Nandini <n@x.test>", "Fri, 02 Oct 2026 04:12:31 +0000", ZonedClock.Default));
    }
}
