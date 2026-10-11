using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

public class Rc11SettingsPiecesTests
{
    private static readonly System.DateTimeOffset Now = new(2026, 10, 2, 4, 30, 0, System.TimeSpan.Zero);

    [Fact]
    public void TheZoneList_HasKolkataLabelledByOffset_AndIsSortedByOffset()
    {
        IReadOnlyList<(string Id, string Label)> zones = ZonedClock.Choices(Now);
        Assert.Contains(zones, z => z.Id == "Asia/Kolkata" && z.Label == "(UTC+05:30) Asia - Kolkata");
        Assert.Contains(zones, z => z.Id == "UTC");
        int kolkata = zones.ToList().FindIndex(z => z.Id == "Asia/Kolkata");
        int london = zones.ToList().FindIndex(z => z.Id == "Europe/London");
        Assert.True(london < kolkata);
        Assert.All(zones, z => Assert.True(ZonedClock.For(z.Id, null).ZoneId == z.Id || z.Id == "UTC", z.Id));
    }

    [Theory]
    [InlineData("Asia/Calcutta", "Asia/Kolkata")]
    [InlineData("Asia/Katmandu", "Asia/Kathmandu")]
    [InlineData("Europe/Kiev", "Europe/Kyiv")]
    [InlineData("Asia/Kolkata", "Asia/Kolkata")]
    [InlineData("Europe/London", "Europe/London")]
    public void OldZoneNames_BecomeTheCurrentOnes_AsWindowsGivesTheOldOnes(string old, string current)
    {
        // DEF-091: on Windows, India's zone came through as Asia/Calcutta and the
        // list had no Asia/Kolkata - so nobody in India saw their zone selected.
        Assert.Equal(current, ZonedClock.Modern(old));
    }

    [Fact]
    public void TheZoneList_NeverHasTheOldNames()
    {
        Assert.DoesNotContain(ZonedClock.Choices(Now), z => z.Id is "Asia/Calcutta" or "Asia/Katmandu" or "Asia/Saigon");
    }

    [Theory]
    [InlineData("en", "A")]
    [InlineData("ta", "\u0B85")]
    [InlineData("ml", "\u0D05")]
    [InlineData("hi", "\u0905")]
    [InlineData("mr", "\u0905")]
    [InlineData("gu", "\u0A85")]
    [InlineData(null, "A")]
    public void TheMark_CarriesTheFirstLetterOfTheReadersLanguage(string? code, string letter)
    {
        // SPEC-11 item 39: "every script".
        Assert.Equal(letter, Words.MarkLetter(code));
    }
}
