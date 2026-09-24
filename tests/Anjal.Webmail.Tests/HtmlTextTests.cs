using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

public class HtmlTextTests
{
    [Fact]
    public void ToPlain_KeepsLinesListsAndQuotes()
    {
        string plain = HtmlText.ToPlain("<p>Dear Dr Rao,</p><p>Results:<br>Hb 13.2</p><ul><li>CBC</li><li>LFT</li></ul><blockquote>Original line</blockquote>");
        Assert.Equal("Dear Dr Rao,\nResults:\nHb 13.2\n\n• CBC\n• LFT\n\n> Original line", plain);
    }

    [Fact]
    public void ToPlain_DecodesEntities()
    {
        Assert.Equal("A & B < C", HtmlText.ToPlain("A &amp; B &lt; C"));
    }

    [Fact]
    public void FromQuotedPlain_TurnsQuotedLinesIntoABlockquote_AndEscapesTheRest()
    {
        string html = HtmlText.FromQuotedPlain("On Monday, Lab wrote:\n> Hb 13.2\n> <script>");
        Assert.Equal("<div>On Monday, Lab wrote:</div><blockquote>Hb 13.2<br>&lt;script&gt;</blockquote>", html);
    }

    [Fact]
    public void DEF034_NumberedListsKeepTheirNumbers_BulletsStayBullets()
    {
        string plain = HtmlText.ToPlain("<ol><li>Take 5 ml</li><li>Wait one hour</li><li>Repeat</li></ol><ul><li>Note</li></ul><ol><li>Again</li></ol>");
        Assert.Contains("1. Take 5 ml\n2. Wait one hour\n3. Repeat", plain, StringComparison.Ordinal);
        Assert.Contains("• Note", plain, StringComparison.Ordinal);
        Assert.Contains("1. Again", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void DEF034_AParagraphAfterAListStartsOnItsOwnLine()
    {
        string plain = HtmlText.ToPlain("<ol><li>Take 5 ml</li><li>Repeat once</li></ol><p>Notes: bring the card.</p>");
        Assert.Contains("2. Repeat once\nNotes: bring the card.", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("Repeat onceNotes", plain, StringComparison.Ordinal);
    }
}
