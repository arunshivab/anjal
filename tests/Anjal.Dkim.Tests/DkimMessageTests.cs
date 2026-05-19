using System.Text;
using Anjal.Dkim;

namespace Anjal.Dkim.Tests;

public class DkimMessageTests
{
    [Fact]
    public void Parse_SplitsHeadersAndBody()
    {
        byte[] msg = Encoding.UTF8.GetBytes(
            "From: a@x\r\n" +
            "To: b@y\r\n" +
            "\r\n" +
            "Body line 1\r\nBody line 2\r\n");

        DkimMessage parsed = DkimMessage.Parse(msg);

        Assert.Equal(2, parsed.Headers.Count);
        Assert.Equal("From", parsed.Headers[0].Name);
        Assert.Equal(" a@x", parsed.Headers[0].Value);
        Assert.Equal("To", parsed.Headers[1].Name);
        Assert.Equal("Body line 1\r\nBody line 2\r\n", Encoding.UTF8.GetString(parsed.Body));
    }

    [Fact]
    public void Parse_PreservesFoldedContinuations()
    {
        byte[] msg = Encoding.UTF8.GetBytes(
            "Subject: First line\r\n continuation\r\n" +
            "\r\n" +
            "Body\r\n");

        DkimMessage parsed = DkimMessage.Parse(msg);
        Assert.Single(parsed.Headers);
        // The continuation must be preserved including the CRLF + WSP per RFC 6376 section 3.4.2 step 2.
        Assert.Equal(" First line\r\n continuation", parsed.Headers[0].Value);
    }

    [Fact]
    public void Parse_NormalizesBareLfToCrlf()
    {
        // Some inputs use bare LF; parser should handle them.
        byte[] msg = Encoding.UTF8.GetBytes("From: a@x\nTo: b@y\n\nBody\n");
        DkimMessage parsed = DkimMessage.Parse(msg);
        Assert.Equal(2, parsed.Headers.Count);
        Assert.Equal("Body\r\n", Encoding.UTF8.GetString(parsed.Body));
    }

    [Fact]
    public void Parse_ThrowsOnMissingSeparator()
    {
        byte[] msg = Encoding.UTF8.GetBytes("From: a@x\r\nTo: b@y\r\n");
        Assert.Throws<System.FormatException>(() => DkimMessage.Parse(msg));
    }

    [Fact]
    public void GetHeaderValue_CaseInsensitive()
    {
        byte[] msg = Encoding.UTF8.GetBytes("Subject: Hello\r\n\r\nBody\r\n");
        DkimMessage parsed = DkimMessage.Parse(msg);
        Assert.Equal(" Hello", parsed.GetHeaderValue("subject"));
        Assert.Equal(" Hello", parsed.GetHeaderValue("SUBJECT"));
        Assert.Null(parsed.GetHeaderValue("Missing"));
    }

    [Fact]
    public void GetHeaderValue_ReturnsMostRecent()
    {
        // Per RFC 6376, when a header appears multiple times the verifier
        // typically uses the bottom-most occurrence. Our implementation
        // returns the last-seen instance.
        byte[] msg = Encoding.UTF8.GetBytes("X-Test: first\r\nX-Test: second\r\n\r\nBody\r\n");
        DkimMessage parsed = DkimMessage.Parse(msg);
        Assert.Equal(" second", parsed.GetHeaderValue("X-Test"));
    }
}
