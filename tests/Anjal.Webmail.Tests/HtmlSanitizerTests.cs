using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

public class HtmlSanitizerTests
{
    [Fact]
    public void RemovesScriptWithContent()
    {
        string html = "<p>hi</p><script>alert(1)</script><p>there</p>";
        string clean = HtmlSanitizer.Sanitize(html);
        Assert.Equal("<p>hi</p><p>there</p>", clean);
    }

    [Fact]
    public void RemovesStyleBlocksFormsAndFrames()
    {
        string html = "<style>body{}</style><form action='x'><input name='a'></form><iframe src='http://evil'></iframe><b>ok</b>";
        string clean = HtmlSanitizer.Sanitize(html);
        Assert.Equal("<b>ok</b>", clean);
    }

    [Fact]
    public void StripsEventHandlersAndJavascriptUrls()
    {
        string html = "<a href=\"javascript:alert(1)\" onclick=\"x()\">link</a><img src=\"data:text/html;base64,AAA\" onerror=\"y()\">";
        string clean = HtmlSanitizer.Sanitize(html);
        Assert.DoesNotContain("javascript", clean, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", clean, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", clean, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:text", clean, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<a target=\"_blank\" rel=\"noopener noreferrer nofollow\">link</a>", clean, System.StringComparison.Ordinal);
    }

    [Fact]
    public void BlocksRemoteImagesUnlessAllowed()
    {
        string html = "<img src=\"https://tracker.example/pixel.gif\" alt=\"x\">";
        string blocked = HtmlSanitizer.Sanitize(html);
        Assert.Contains("data-blocked-src=\"https://tracker.example/pixel.gif\"", blocked, System.StringComparison.Ordinal);
        Assert.DoesNotContain(" src=", blocked, System.StringComparison.Ordinal);

        string allowed = HtmlSanitizer.Sanitize(html, allowRemoteImages: true);
        Assert.Contains(" src=\"https://tracker.example/pixel.gif\"", allowed, System.StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsInlineDataImagesAndSafeStyles()
    {
        string html = "<img src=\"data:image/png;base64,iVBORw0KGgo=\" style=\"width:10px\"><span style=\"color:red;background:url(http://x)\">t</span>";
        string clean = HtmlSanitizer.Sanitize(html);
        Assert.Contains("src=\"data:image/png;base64,iVBORw0KGgo=\"", clean, System.StringComparison.Ordinal);
        Assert.Contains("style=\"width:10px\"", clean, System.StringComparison.Ordinal);
        Assert.DoesNotContain("url(", clean, System.StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownTagsAreDroppedButContentKept()
    {
        Assert.Equal("text", HtmlSanitizer.Sanitize("<custom-x>text</custom-x>"));
        Assert.Equal("<p>a</p>", HtmlSanitizer.Sanitize("<!-- c --><p>a</p>"));
    }

    [Fact]
    public void IsSafeUrl_Rules()
    {
        Assert.True(HtmlSanitizer.IsSafeUrl("https://a.b/c"));
        Assert.True(HtmlSanitizer.IsSafeUrl("mailto:x@y"));
        Assert.True(HtmlSanitizer.IsSafeUrl("#top"));
        Assert.True(HtmlSanitizer.IsSafeUrl("data:image/gif;base64,R0lG"));
        Assert.False(HtmlSanitizer.IsSafeUrl("javascript:1"));
        Assert.False(HtmlSanitizer.IsSafeUrl("java\nscript:1"));
        Assert.False(HtmlSanitizer.IsSafeUrl("data:image/svg+xml;base64,PHN2Zz4="));
        Assert.False(HtmlSanitizer.IsSafeUrl("vbscript:x"));
    }

    [Fact]
    public void FromPlainText_EscapesAndPreservesLines()
    {
        string html = HtmlSanitizer.FromPlainText("a < b\r\nline 2");
        Assert.Contains("a &lt; b", html, System.StringComparison.Ordinal);
        Assert.StartsWith("<pre", html, System.StringComparison.Ordinal);
    }
}
