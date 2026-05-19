namespace Anjal.Routing.Tests;

public class HttpWebhookDispatcherTests
{
    [Fact]
    public void ComputeSignature_KnownVector()
    {
        // HMAC-SHA256 of "<timestamp>.<body>" with a 1-byte key (0x00).
        const string secretHex = "00";
        const long timestamp = 1700000000;
        const string body = "{\"x\":1}";

        string sig = HttpWebhookDispatcher.ComputeSignature(secretHex, timestamp, body);
        Assert.Equal(64, sig.Length);
        Assert.Matches("^[0-9a-f]+$", sig);
    }

    [Fact]
    public void ComputeSignature_DifferentBody_DifferentSignature()
    {
        const string secretHex = "deadbeef";
        const long ts = 1700000000;

        string a = HttpWebhookDispatcher.ComputeSignature(secretHex, ts, "body-a");
        string b = HttpWebhookDispatcher.ComputeSignature(secretHex, ts, "body-b");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeSignature_DifferentTimestamp_DifferentSignature()
    {
        const string secretHex = "deadbeef";
        const string body = "same-body";

        string a = HttpWebhookDispatcher.ComputeSignature(secretHex, 1700000000, body);
        string b = HttpWebhookDispatcher.ComputeSignature(secretHex, 1700000001, body);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeSignature_DifferentSecret_DifferentSignature()
    {
        const long ts = 1700000000;
        const string body = "x";

        string a = HttpWebhookDispatcher.ComputeSignature("deadbeef", ts, body);
        string b = HttpWebhookDispatcher.ComputeSignature("deadc0de", ts, body);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeSignature_OddHexLength_Throws()
    {
        Assert.Throws<System.FormatException>(() => HttpWebhookDispatcher.ComputeSignature("abc", 1, "x"));
    }

    [Fact]
    public void ComputeSignature_InvalidHexChar_Throws()
    {
        Assert.Throws<System.FormatException>(() => HttpWebhookDispatcher.ComputeSignature("xyzz", 1, "x"));
    }

    [Fact]
    public void SerializePayload_ProducesValidJson()
    {
        var payload = new WebhookPayload
        {
            InboundMessageId = new System.Guid("00000000-0000-0000-0000-000000000001"),
            Recipient = "reports+x@host",
            LocalPart = "reports",
            Tag = "x",
            Subject = "Hello",
            EnvelopeFrom = "alice@example",
            MessageId = "abc@example",
            ReceivedAt = new System.DateTimeOffset(2026, 5, 19, 12, 0, 0, System.TimeSpan.Zero),
            RawBytesBase64 = "SGVsbG8=",
        };

        string json = HttpWebhookDispatcher.SerializePayload(payload);

        Assert.StartsWith("{", json, System.StringComparison.Ordinal);
        Assert.EndsWith("}", json, System.StringComparison.Ordinal);
        Assert.Contains("\"inboundMessageId\":\"00000000-0000-0000-0000-000000000001\"", json, System.StringComparison.Ordinal);
        Assert.Contains("\"recipient\":\"reports+x@host\"", json, System.StringComparison.Ordinal);
        Assert.Contains("\"tag\":\"x\"", json, System.StringComparison.Ordinal);
        Assert.Contains("\"subject\":\"Hello\"", json, System.StringComparison.Ordinal);
    }

    [Fact]
    public void SerializePayload_EscapesSpecialChars()
    {
        var payload = new WebhookPayload
        {
            Subject = "Has \"quotes\" and \\slash\\ and \n newline",
        };
        string json = HttpWebhookDispatcher.SerializePayload(payload);

        Assert.Contains("\\\"quotes\\\"", json, System.StringComparison.Ordinal);
        Assert.Contains("\\\\slash\\\\", json, System.StringComparison.Ordinal);
        Assert.Contains("\\n", json, System.StringComparison.Ordinal);
    }
}
