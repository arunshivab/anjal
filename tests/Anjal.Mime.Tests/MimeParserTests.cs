namespace Anjal.Mime.Tests;

public class MimeParserTests
{
    [Fact]
    public void Parse_SimpleMessage_HeadersAndBody()
    {
        string raw = "From: alice@example.com\r\n" +
                     "To: bob@example.com\r\n" +
                     "Subject: Test\r\n" +
                     "\r\n" +
                     "Hello, body!\r\n";
        var msg = MimeParser.Parse(raw);

        Assert.Equal("Test", msg.Subject);
        Assert.Single(msg.From);
        Assert.Equal("alice@example.com", msg.From[0].Address);
        Assert.Single(msg.To);
        Assert.Equal("bob@example.com", msg.To[0].Address);

        var part = Assert.IsType<MimePart>(msg.Body);
        Assert.StartsWith("Hello, body!", part.GetBodyAsText(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_FoldedHeader_Unfolded()
    {
        string raw = "Subject: Hello\r\n there\r\n\r\nBody";
        var msg = MimeParser.Parse(raw);
        Assert.Equal("Hello there", msg.Headers.Get("Subject"));
    }

    [Fact]
    public void Parse_FoldedHeaderWithTab_Unfolded()
    {
        string raw = "Subject: Hello\r\n\tworld\r\n\r\nBody";
        var msg = MimeParser.Parse(raw);
        Assert.Equal("Hello world", msg.Headers.Get("Subject"));
    }

    [Fact]
    public void Parse_BareLfLineEndings_Tolerated()
    {
        // Pragmatic: some senders use bare LF. Parser should still work.
        string raw = "From: alice@example.com\nTo: bob@example.com\n\nBody";
        var msg = MimeParser.Parse(raw);
        Assert.Single(msg.From);
        Assert.Equal("alice@example.com", msg.From[0].Address);
    }

    [Fact]
    public void Parse_EncodedWordSubject_Decoded()
    {
        string raw = "Subject: =?utf-8?B?SGVsbG8gV29ybGQ=?=\r\n\r\nBody";
        var msg = MimeParser.Parse(raw);
        Assert.Equal("Hello World", msg.Subject);
    }

    [Fact]
    public void Parse_Base64Body_Decoded()
    {
        // "Hello" base64-encoded is "SGVsbG8="
        string raw = "Content-Type: text/plain; charset=utf-8\r\n" +
                     "Content-Transfer-Encoding: base64\r\n" +
                     "\r\n" +
                     "SGVsbG8=\r\n";
        var msg = MimeParser.Parse(raw);
        var part = Assert.IsType<MimePart>(msg.Body);
        Assert.Equal("Hello", part.GetBodyAsText());
    }

    [Fact]
    public void Parse_QuotedPrintableBody_Decoded()
    {
        string raw = "Content-Type: text/plain; charset=utf-8\r\n" +
                     "Content-Transfer-Encoding: quoted-printable\r\n" +
                     "\r\n" +
                     "Caf=C3=A9\r\n";
        var msg = MimeParser.Parse(raw);
        var part = Assert.IsType<MimePart>(msg.Body);
        Assert.Equal("Caf\u00e9", part.GetBodyAsText().TrimEnd('\r', '\n'));
    }

    [Fact]
    public void Parse_Multipart_SplitsIntoChildren()
    {
        string raw = "Content-Type: multipart/mixed; boundary=BOUNDARY\r\n" +
                     "\r\n" +
                     "preamble text\r\n" +
                     "--BOUNDARY\r\n" +
                     "Content-Type: text/plain\r\n" +
                     "\r\n" +
                     "part one\r\n" +
                     "--BOUNDARY\r\n" +
                     "Content-Type: text/html\r\n" +
                     "\r\n" +
                     "<p>part two</p>\r\n" +
                     "--BOUNDARY--\r\n" +
                     "epilogue\r\n";

        var msg = MimeParser.Parse(raw);
        var multi = Assert.IsType<MimeMultipart>(msg.Body);
        Assert.Equal(2, multi.Parts.Count);

        var first = Assert.IsType<MimePart>(multi.Parts[0]);
        Assert.Equal("text/plain", first.ContentType.MimeType);
        Assert.StartsWith("part one", first.GetBodyAsText(), System.StringComparison.Ordinal);

        var second = Assert.IsType<MimePart>(multi.Parts[1]);
        Assert.Equal("text/html", second.ContentType.MimeType);
        Assert.StartsWith("<p>part two</p>", second.GetBodyAsText(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_NestedMultipart_Recursive()
    {
        string raw = "Content-Type: multipart/mixed; boundary=OUTER\r\n" +
                     "\r\n" +
                     "--OUTER\r\n" +
                     "Content-Type: multipart/alternative; boundary=INNER\r\n" +
                     "\r\n" +
                     "--INNER\r\n" +
                     "Content-Type: text/plain\r\n" +
                     "\r\n" +
                     "plain version\r\n" +
                     "--INNER\r\n" +
                     "Content-Type: text/html\r\n" +
                     "\r\n" +
                     "<p>html version</p>\r\n" +
                     "--INNER--\r\n" +
                     "--OUTER--\r\n";

        var msg = MimeParser.Parse(raw);
        var outer = Assert.IsType<MimeMultipart>(msg.Body);
        Assert.Single(outer.Parts);
        var inner = Assert.IsType<MimeMultipart>(outer.Parts[0]);
        Assert.Equal("multipart/alternative", inner.ContentType.MimeType);
        Assert.Equal(2, inner.Parts.Count);
    }

    [Fact]
    public void Parse_MultipleReceivedHeaders_AllPreserved()
    {
        string raw = "Received: from server1\r\n" +
                     "Received: from server2\r\n" +
                     "Received: from server3\r\n" +
                     "\r\nbody";
        var msg = MimeParser.Parse(raw);
        Assert.Equal(3, msg.Headers.GetAll("Received").Count);
    }

    [Fact]
    public void Parse_EmptyHeaderSection_BodyStillReadable()
    {
        // An empty header section: just the blank-line separator,
        // followed immediately by body content. Pragmatic acceptance of
        // malformed input - some test harnesses produce this shape.
        string raw = "\r\n\r\nJust a body";
        var msg = MimeParser.Parse(raw);
        var part = Assert.IsType<MimePart>(msg.Body);
        Assert.StartsWith("Just a body", part.GetBodyAsText(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_MalformedHeaderWithoutColon_Skipped()
    {
        string raw = "From: alice@example.com\r\nThis is not a header\r\nTo: bob@example.com\r\n\r\nbody";
        var msg = MimeParser.Parse(raw);
        Assert.Single(msg.From);
        Assert.Single(msg.To);
    }

    [Fact]
    public void Parse_MessageId_AngleBracketsStripped()
    {
        string raw = "Message-ID: <abc123@example.com>\r\n\r\n";
        var msg = MimeParser.Parse(raw);
        Assert.Equal("abc123@example.com", msg.MessageId);
    }
}
