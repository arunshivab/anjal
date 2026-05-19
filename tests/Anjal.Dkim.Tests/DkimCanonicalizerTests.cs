using System.Text;
using Anjal.Dkim;

namespace Anjal.Dkim.Tests;

public class DkimCanonicalizerTests
{
    // ===== Relaxed body canonicalization (RFC 6376 section 3.4.4 / 3.4.5 example) =====

    [Fact]
    public void RelaxedBody_RFC6376_Example()
    {
        // RFC 6376 section 3.4.5: input " C \r\nD \t E\r\n\r\n\r\n"
        // expected output " C\r\nD E\r\n"
        byte[] input = Encoding.UTF8.GetBytes(" C \r\nD \t E\r\n\r\n\r\n");
        byte[] result = DkimCanonicalizer.CanonBody(input, BodyCanonicalization.Relaxed);
        Assert.Equal(" C\r\nD E\r\n", Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void RelaxedBody_EmptyBody_ReturnsEmpty()
    {
        byte[] result = DkimCanonicalizer.CanonBody(System.Array.Empty<byte>(), BodyCanonicalization.Relaxed);
        Assert.Empty(result);
    }

    [Fact]
    public void RelaxedBody_OnlyEmptyLines_ReturnsEmpty()
    {
        byte[] input = Encoding.UTF8.GetBytes("\r\n\r\n\r\n");
        byte[] result = DkimCanonicalizer.CanonBody(input, BodyCanonicalization.Relaxed);
        Assert.Empty(result);
    }

    [Fact]
    public void RelaxedBody_StripsTrailingWhitespace()
    {
        byte[] input = Encoding.UTF8.GetBytes("hello   \r\nworld\t\t\r\n");
        byte[] result = DkimCanonicalizer.CanonBody(input, BodyCanonicalization.Relaxed);
        Assert.Equal("hello\r\nworld\r\n", Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void RelaxedBody_CollapsesInternalWhitespace()
    {
        byte[] input = Encoding.UTF8.GetBytes("hello\t\tworld   foo\r\n");
        byte[] result = DkimCanonicalizer.CanonBody(input, BodyCanonicalization.Relaxed);
        Assert.Equal("hello world foo\r\n", Encoding.UTF8.GetString(result));
    }

    // ===== Simple body canonicalization (RFC 6376 section 3.4.3) =====

    [Fact]
    public void SimpleBody_EmptyBody_ReturnsSingleCrlf()
    {
        byte[] result = DkimCanonicalizer.CanonBody(System.Array.Empty<byte>(), BodyCanonicalization.Simple);
        Assert.Equal("\r\n", Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void SimpleBody_StripsTrailingEmptyLines()
    {
        byte[] input = Encoding.UTF8.GetBytes("Hello\r\n\r\n\r\n");
        byte[] result = DkimCanonicalizer.CanonBody(input, BodyCanonicalization.Simple);
        Assert.Equal("Hello\r\n", Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void SimpleBody_PreservesInternalWhitespace()
    {
        byte[] input = Encoding.UTF8.GetBytes("Hello   World\r\n\tIndented\r\n");
        byte[] result = DkimCanonicalizer.CanonBody(input, BodyCanonicalization.Simple);
        Assert.Equal("Hello   World\r\n\tIndented\r\n", Encoding.UTF8.GetString(result));
    }

    // ===== Relaxed header canonicalization (RFC 6376 section 3.4.2) =====

    [Fact]
    public void RelaxedHeader_LowercasesName()
    {
        string result = DkimCanonicalizer.CanonHeader("Subject", " Hello", HeaderCanonicalization.Relaxed);
        Assert.Equal("subject:Hello\r\n", result);
    }

    [Fact]
    public void RelaxedHeader_StripsLeadingValueWhitespace()
    {
        string result = DkimCanonicalizer.CanonHeader("From", "    Joe <joe@example.com>", HeaderCanonicalization.Relaxed);
        Assert.Equal("from:Joe <joe@example.com>\r\n", result);
    }

    [Fact]
    public void RelaxedHeader_StripsTrailingValueWhitespace()
    {
        string result = DkimCanonicalizer.CanonHeader("To", " a@b.com  \t  ", HeaderCanonicalization.Relaxed);
        Assert.Equal("to:a@b.com\r\n", result);
    }

    [Fact]
    public void RelaxedHeader_CollapsesInternalWhitespace()
    {
        string result = DkimCanonicalizer.CanonHeader("Subject", " Multi   Space\tHeader", HeaderCanonicalization.Relaxed);
        Assert.Equal("subject:Multi Space Header\r\n", result);
    }

    [Fact]
    public void RelaxedHeader_UnfoldsContinuationLines()
    {
        // A header value with a folded continuation (CRLF SP).
        string result = DkimCanonicalizer.CanonHeader(
            "Subject",
            " First line\r\n continuation",
            HeaderCanonicalization.Relaxed);
        Assert.Equal("subject:First line continuation\r\n", result);
    }

    // ===== Simple header canonicalization =====

    [Fact]
    public void SimpleHeader_PreservesCaseAndWhitespace()
    {
        string result = DkimCanonicalizer.CanonHeader("Subject", " Hello", HeaderCanonicalization.Simple);
        Assert.Equal("Subject: Hello\r\n", result);
    }
}
