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
}
