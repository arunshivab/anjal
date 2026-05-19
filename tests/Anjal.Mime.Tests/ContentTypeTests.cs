namespace Anjal.Mime.Tests;

public class ContentTypeTests
{
    [Fact]
    public void Parse_TextPlainOnly()
    {
        var ct = ContentType.Parse("text/plain");
        Assert.Equal("text", ct.MediaType);
        Assert.Equal("plain", ct.SubType);
        Assert.Equal("text/plain", ct.MimeType);
        Assert.Empty(ct.Parameters);
    }

    [Fact]
    public void Parse_WithCharsetParameter()
    {
        var ct = ContentType.Parse("text/plain; charset=utf-8");
        Assert.Equal("utf-8", ct.Charset);
    }

    [Fact]
    public void Parse_WithQuotedBoundary()
    {
        var ct = ContentType.Parse("multipart/mixed; boundary=\"abc; xyz\"");
        Assert.Equal("abc; xyz", ct.Boundary);
    }

    [Fact]
    public void Parse_CaseInsensitiveParameterNames()
    {
        var ct = ContentType.Parse("text/plain; Charset=utf-8");
        Assert.Equal("utf-8", ct.Charset);
    }

    [Fact]
    public void Parse_Multipart_IsMultipartTrue()
    {
        var ct = ContentType.Parse("multipart/mixed; boundary=xyz");
        Assert.True(ct.IsMultipart);
        Assert.False(ct.IsText);
    }

    [Fact]
    public void Parse_NullOrInvalid_ReturnsDefault()
    {
        var ct = ContentType.Parse(null);
        Assert.Equal("text", ct.MediaType);
        Assert.Equal("plain", ct.SubType);
        Assert.Equal("us-ascii", ct.Charset);
    }

    [Fact]
    public void Parse_MultipleParameters()
    {
        var ct = ContentType.Parse("multipart/mixed; charset=utf-8; boundary=xyz");
        Assert.Equal("utf-8", ct.Charset);
        Assert.Equal("xyz", ct.Boundary);
    }

    [Fact]
    public void ToHeaderValue_RoundTripsParameters()
    {
        var ct = ContentType.Parse("multipart/mixed; boundary=abc123");
        string rendered = ct.ToHeaderValue();
        var reparsed = ContentType.Parse(rendered);
        Assert.Equal("abc123", reparsed.Boundary);
        Assert.Equal("multipart/mixed", reparsed.MimeType);
    }

    [Fact]
    public void ToHeaderValue_QuotesValuesWithSpecials()
    {
        var ct = new ContentType("multipart", "mixed", new System.Collections.Generic.Dictionary<string, string>
        {
            ["boundary"] = "value with space",
        });
        string rendered = ct.ToHeaderValue();
        Assert.Contains("\"value with space\"", rendered, System.StringComparison.Ordinal);
    }
}

public class EncodedWordDecoderTests
{
    [Fact]
    public void Decode_PlainAscii_PassesThrough()
    {
        Assert.Equal("Hello", EncodedWordDecoder.Decode("Hello"));
    }

    [Fact]
    public void Decode_Base64EncodedWord()
    {
        // =?utf-8?B?SGVsbG8gV29ybGQ=?=  →  "Hello World"
        Assert.Equal("Hello World", EncodedWordDecoder.Decode("=?utf-8?B?SGVsbG8gV29ybGQ=?="));
    }

    [Fact]
    public void Decode_QEncodedWord()
    {
        // =?utf-8?Q?Hello=20World?=
        Assert.Equal("Hello World", EncodedWordDecoder.Decode("=?utf-8?Q?Hello=20World?="));
    }

    [Fact]
    public void Decode_QEncodedWord_UnderscoreIsSpace()
    {
        Assert.Equal("Hello World", EncodedWordDecoder.Decode("=?utf-8?Q?Hello_World?="));
    }

    [Fact]
    public void Decode_AdjacentEncodedWords_WhitespaceRemovedBetween()
    {
        // RFC 2047 section 6.2: whitespace between adjacent encoded words is dropped.
        string input = "=?utf-8?B?SGVsbG8=?= =?utf-8?B?V29ybGQ=?=";
        Assert.Equal("HelloWorld", EncodedWordDecoder.Decode(input));
    }

    [Fact]
    public void Decode_TextSurroundingEncodedWord_Preserved()
    {
        string input = "Re: =?utf-8?B?SGVsbG8=?= today";
        Assert.Equal("Re: Hello today", EncodedWordDecoder.Decode(input));
    }

    [Fact]
    public void Decode_MalformedEncodedWord_PassedThrough()
    {
        // Missing closing "?=" - decoder should leave it alone.
        string input = "=?utf-8?B?broken";
        Assert.Equal("=?utf-8?B?broken", EncodedWordDecoder.Decode(input));
    }

    [Fact]
    public void Decode_UnknownCharset_FallsBackToUtf8()
    {
        // Charset that doesn't exist - decoder falls back to UTF-8.
        string input = "=?made-up-charset?B?SGVsbG8=?=";
        Assert.Equal("Hello", EncodedWordDecoder.Decode(input));
    }
}
