namespace Anjal.Mime.Tests;

public class MimeBuilderTests
{
    [Fact]
    public void Build_SimpleMessage_ProducesCrlfWireFormat()
    {
        var msg = new MimeMessage();
        msg.Headers.Add("From", "alice@example.com");
        msg.Headers.Add("To", "bob@example.com");
        msg.Subject = "Test";
        msg.Headers.Add("Content-Type", "text/plain; charset=utf-8");

        var part = (MimePart)msg.Body;
        part.SetBodyAsText("Hello body!");

        byte[] bytes = MimeBuilder.Build(msg);
        string output = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.Contains("From: alice@example.com\r\n", output, System.StringComparison.Ordinal);
        Assert.Contains("To: bob@example.com\r\n", output, System.StringComparison.Ordinal);
        Assert.Contains("Subject: Test\r\n", output, System.StringComparison.Ordinal);
        Assert.Contains("\r\n\r\nHello body!", output, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Base64Body_EncodesOnWrite()
    {
        var msg = new MimeMessage();
        msg.Headers.Add("Content-Type", "application/octet-stream");
        msg.Headers.Add("Content-Transfer-Encoding", "base64");
        ((MimePart)msg.Body).Body = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        byte[] bytes = MimeBuilder.Build(msg);
        string output = System.Text.Encoding.UTF8.GetString(bytes);

        // 0xDEADBEEF in base64 is "3q2+7w=="
        Assert.Contains("3q2+7w==", output, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Multipart_IncludesBoundariesAndClosing()
    {
        var multi = MultipartFactory.Create("mixed");
        string boundary = multi.ContentType.Boundary!;

        var first = new MimePart();
        first.Headers.Add("Content-Type", "text/plain");
        first.SetBodyAsText("one");
        multi.Parts.Add(first);

        var second = new MimePart();
        second.Headers.Add("Content-Type", "text/plain");
        second.SetBodyAsText("two");
        multi.Parts.Add(second);

        var msg = new MimeMessage(multi);
        byte[] bytes = MimeBuilder.Build(msg);
        string output = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.Contains("--" + boundary + "\r\n", output, System.StringComparison.Ordinal);
        Assert.Contains("--" + boundary + "--\r\n", output, System.StringComparison.Ordinal);
        Assert.Contains("one", output, System.StringComparison.Ordinal);
        Assert.Contains("two", output, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Build_LongHeader_Folded()
    {
        var msg = new MimeMessage();
        string longValue = string.Join(", ", System.Linq.Enumerable.Range(0, 20).Select(i => $"address{i}@example.com"));
        msg.Headers.Add("To", longValue);

        byte[] bytes = MimeBuilder.Build(msg);
        string output = System.Text.Encoding.UTF8.GetString(bytes);

        // Folded continuation lines start with a space (RFC 5322 section 2.2.3).
        Assert.Contains("\r\n ", output, System.StringComparison.Ordinal);
    }
}

public class RoundTripTests
{
    [Fact]
    public void RoundTrip_SimpleMessage_HeadersAndBodyPreserved()
    {
        string raw = "From: alice@example.com\r\n" +
                     "To: bob@example.com\r\n" +
                     "Subject: Test\r\n" +
                     "Content-Type: text/plain; charset=utf-8\r\n" +
                     "\r\n" +
                     "Hello, body!\r\n";
        var msg = MimeParser.Parse(raw);
        byte[] rebuilt = MimeBuilder.Build(msg);
        var msg2 = MimeParser.Parse(rebuilt);

        Assert.Equal(msg.Subject, msg2.Subject);
        Assert.Equal(msg.From[0].Address, msg2.From[0].Address);
        Assert.Equal(msg.To[0].Address, msg2.To[0].Address);
    }

    [Fact]
    public void RoundTrip_Base64Body_BinaryIdentical()
    {
        var msg = new MimeMessage();
        msg.Headers.Add("Content-Type", "application/octet-stream");
        msg.Headers.Add("Content-Transfer-Encoding", "base64");

        byte[] payload = new byte[200];
        var rng = new System.Random(7);
        rng.NextBytes(payload);
        ((MimePart)msg.Body).Body = payload;

        byte[] wire = MimeBuilder.Build(msg);
        var reparsed = MimeParser.Parse(wire);
        var part = Assert.IsType<MimePart>(reparsed.Body);

        Assert.Equal(payload, part.Body);
    }

    [Fact]
    public void RoundTrip_Multipart_StructurePreserved()
    {
        var multi = MultipartFactory.Create("mixed");

        var text = new MimePart();
        text.Headers.Add("Content-Type", "text/plain; charset=utf-8");
        text.SetBodyAsText("plain text here");
        multi.Parts.Add(text);

        var data = new MimePart();
        data.Headers.Add("Content-Type", "application/octet-stream");
        data.Headers.Add("Content-Transfer-Encoding", "base64");
        data.Body = new byte[] { 1, 2, 3, 4, 5 };
        multi.Parts.Add(data);

        var msg = new MimeMessage(multi);
        msg.Headers.Add("From", "alice@example.com");

        byte[] wire = MimeBuilder.Build(msg);
        var reparsed = MimeParser.Parse(wire);
        var rebuiltMulti = Assert.IsType<MimeMultipart>(reparsed.Body);

        Assert.Equal(2, rebuiltMulti.Parts.Count);
        var rebuiltText = Assert.IsType<MimePart>(rebuiltMulti.Parts[0]);
        Assert.StartsWith("plain text here", rebuiltText.GetBodyAsText(), System.StringComparison.Ordinal);

        var rebuiltData = Assert.IsType<MimePart>(rebuiltMulti.Parts[1]);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, rebuiltData.Body);
    }

    [Fact]
    public void RoundTrip_QuotedPrintable_PreservedThroughCycle()
    {
        var msg = new MimeMessage();
        msg.Headers.Add("Content-Type", "text/plain; charset=utf-8");
        msg.Headers.Add("Content-Transfer-Encoding", "quoted-printable");
        ((MimePart)msg.Body).SetBodyAsText("Caf\u00e9 and \u20ac currency");

        byte[] wire = MimeBuilder.Build(msg);
        var reparsed = MimeParser.Parse(wire);
        var part = Assert.IsType<MimePart>(reparsed.Body);

        Assert.Equal("Caf\u00e9 and \u20ac currency", part.GetBodyAsText().TrimEnd('\r', '\n'));
    }
}
