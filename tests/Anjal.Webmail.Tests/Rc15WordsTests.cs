using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

/// <summary>
/// rc.15: the words of the dashboards and the list's headings - month
/// headings are a word and a year, and lines made as English with blanks
/// are looked up before the blanks are filled.
/// </summary>
public sealed class Rc15WordsTests
{
    private const string TamilSeptember = "செப்டம்பர்";
    private const string TamilToday = "இன்று";
    private const string TamilDrafts = "{n} வரைவுகள்";

    private static Lexicon Tamil() => new Words(new Dictionary<string, IReadOnlyDictionary<string, string>>
    {
        ["ta"] = new Dictionary<string, string>
        {
            ["September"] = TamilSeptember,
            ["Today"] = TamilToday,
            ["{n} drafts not finished"] = TamilDrafts,
        },
    }).For("ta");

    [Fact]
    public void Heading_TranslatesTheMonth_AndKeepsTheYear()
    {
        Lexicon ta = Tamil();
        Assert.Equal(TamilSeptember + " 2026", ta.Heading("September 2026"));
        Assert.Equal(TamilToday, ta.Heading("Today"));
        Assert.Equal("Earlier this week", ta.Heading("Earlier this week"));
        Assert.Equal("Sept 2026", ta.Heading("Sept 2026"));
        Assert.Equal("September next", ta.Heading("September next"));
    }

    [Fact]
    public void ALineMadeLater_IsLookedUp_ThenFilled()
    {
        var line = new DashLine("edit", "{n} drafts not finished", string.Empty, "/folder/Drafts", string.Empty, DashLine.N(1234));
        string text = HealthTile.Fill(Tamil()[line.Text], line.Args);
        Assert.Equal(TamilDrafts.Replace("{n}", "1,234", StringComparison.Ordinal), text);
    }

    [Fact]
    public void TheEnglishList_HasEveryMonth()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Anjal.Webmail", "Resources", "Words", "en.json");
        string json = File.ReadAllText(path);
        foreach (string month in System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.MonthNames.Where(m => m.Length > 0))
        {
            Assert.Contains("\"" + month + "\": \"" + month + "\"", json, StringComparison.Ordinal);
        }
    }
}
