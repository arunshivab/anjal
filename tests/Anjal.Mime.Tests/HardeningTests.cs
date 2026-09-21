using System.Text;

namespace Anjal.Mime.Tests;

public class HardeningTests
{
    private static byte[] Nested(int levels)
    {
        var sb = new StringBuilder();
        sb.Append("From: a@b.test\r\nSubject: nest\r\n");
        for (int i = 0; i < levels; i++)
        {
            // The trailing "x" keeps boundary "b1" from matching inside "b10".
            sb.Append("Content-Type: multipart/mixed; boundary=\"b").Append(i).Append("x\"\r\n\r\n--b").Append(i).Append("x\r\n");
        }
        sb.Append("Content-Type: text/plain\r\n\r\nleaf\r\n");
        for (int i = levels - 1; i >= 0; i--)
        {
            sb.Append("--b").Append(i).Append("x--\r\n");
        }
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void ModestNesting_Parses()
    {
        MimeMessage m = MimeParser.Parse(Nested(10));
        Assert.IsType<MimeMultipart>(m.Body);
    }

    [Fact]
    public void NestingBeyondTheLimit_IsRefused_NotAStackOverflow()
    {
        Assert.Throws<MimeParseException>(() => MimeParser.Parse(Nested(MimeParser.MaxNestingDepth + 1)));
    }

    [Fact]
    public void ExtremeNesting_IsRefusedQuickly()
    {
        // The audit's probe: 100,000 levels (~7 MB) used to overflow the stack
        // and abort the whole process. It must now fail as an ordinary error.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<MimeParseException>(() => MimeParser.Parse(Nested(100_000)));
        Assert.True(sw.Elapsed < System.TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void TooManyParts_IsRefused()
    {
        var sb = new StringBuilder("Content-Type: multipart/mixed; boundary=\"x\"\r\n\r\n");
        for (int i = 0; i < MimeParser.MaxParts + 5; i++)
        {
            sb.Append("--x\r\nContent-Type: text/plain\r\n\r\np\r\n");
        }
        sb.Append("--x--\r\n");
        Assert.Throws<MimeParseException>(() => MimeParser.Parse(Encoding.ASCII.GetBytes(sb.ToString())));
    }

    [Fact]
    public void MimeParseException_IsAFormatException_SoExistingHandlersCatchIt()
    {
        Assert.IsAssignableFrom<System.FormatException>(new MimeParseException("x"));
    }

    [Theory]
    [InlineData("abc\r\nFrom: ceo@other.test")]
    [InlineData("abc\nX-Injected: yes")]
    [InlineData("abc\rX: y")]
    [InlineData("abc\0def")]
    public void HeaderValue_WithLineBreakOrNul_IsRefused(string value)
    {
        Assert.Throws<System.ArgumentException>(() => new MimeHeader("In-Reply-To", value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bad Name")]
    [InlineData("Bad:Name")]
    [InlineData("Bad\r\nName")]
    public void HeaderName_ThatIsNotAToken_IsRefused(string name)
    {
        Assert.Throws<System.ArgumentException>(() => new MimeHeader(name, "v"));
    }

    [Fact]
    public void Neutralise_ReplacesBreaksWithSpaces_AndLeavesCleanValuesAlone()
    {
        Assert.Equal("a b c d", MimeHeader.Neutralise("a\rb\nc\0d"));
        const string clean = "Discharge summary";
        Assert.Same(clean, MimeHeader.Neutralise(clean));
    }

    [Fact]
    public void Parser_NeutralisesNulInIncomingHeaders_RatherThanRefusingTheMail()
    {
        byte[] raw = Encoding.ASCII.GetBytes("Subject: odd\0value\r\nX Bad: dropped\r\n\r\nbody\r\n");
        MimeMessage m = MimeParser.Parse(raw);
        Assert.Equal("odd value", m.Subject);
        Assert.Null(m.Headers.Get("X Bad"));
    }
}
