using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

public class Rc11RecipientsTests
{
    private static readonly string[] Ours = { "anjal.co.in" };
    private static readonly string[] ThreeNames = { "Meera", "Ravi", "Lab" };
    private static readonly string[] ThreeLines = { "To", "To", "Cc" };

    [Fact]
    public void UpToFive_AreShownInFull_InTheOrderWritten_ToBeforeCc()
    {
        Recipients r = Recipients.Of("Meera <m@anjal.co.in>, Ravi <r@anjal.co.in>", "Lab <lab@partner.example>", "arun@anjal.co.in", Ours);
        Assert.False(r.Collapsed);
        Assert.Equal(ThreeNames, r.People.Select(p => p.Name));
        Assert.Equal(ThreeLines, r.People.Select(p => p.Line));
        Assert.Equal(2, r.InsideCount);
        Assert.Equal(1, r.OutsideCount);
    }

    [Fact]
    public void AboveFive_Collapse_WithYouFirst_AndTheSummaryCounts()
    {
        string to = string.Join(", ", Enumerable.Range(1, 8).Select(i => $"Person {i} <p{i}@anjal.co.in>")) + ", Arun <arun@anjal.co.in>";
        Recipients r = Recipients.Of(to, "x@outside.example, y@outside.example, z@elsewhere.example", "ARUN@anjal.co.in", Ours);
        Assert.True(r.Collapsed);
        Assert.Equal(12, r.Count);
        Assert.True(r.People[0].IsYou);
        Assert.Equal(9, r.InsideCount);
        Assert.Equal(3, r.OutsideCount);
        Assert.Equal("anjal.co.in", r.ByOrganisation.First().Key);
    }

    [Fact]
    public void SomeoneInBothToAndCc_IsCountedOnce_AndEncodedNamesAreDecoded()
    {
        Recipients r = Recipients.Of("=?UTF-8?B?4K6u4K+A4K6w4K6+?= <meera@anjal.co.in>", "meera@anjal.co.in", null, Ours);
        Assert.Single(r.People);
        Assert.Equal("\u0BAE\u0BC0\u0BB0\u0BBE", r.People[0].Name);
    }

    [Fact]
    public void WithNoRecipients_NothingCollapses()
    {
        Recipients r = Recipients.Of(string.Empty, null, "arun@anjal.co.in", Ours);
        Assert.Equal(0, r.Count);
        Assert.False(r.Collapsed);
    }
}
