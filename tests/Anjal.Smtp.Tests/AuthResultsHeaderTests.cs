using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Anjal.Smtp.Tests;

/// <summary>DEF-038: a sender cannot plant a verdict under this server's name.</summary>
public sealed class AuthResultsHeaderTests
{
    [Fact]
    public void ForgedClaimsUnderOurName_AreRemoved_OthersAndTheBodyKept()
    {
        string raw = "Authentication-Results: mx.anjal.test; dmarc=pass\r\n" +
                     "Subject: x\r\n" +
                     "Authentication-Results: MX.ANJAL.TEST 1;\r\n dmarc=pass (folded)\r\n" +
                     "Authentication-Results: mx.google.com; dmarc=pass\r\n" +
                     "\r\nBody line: Authentication-Results: mx.anjal.test; dmarc=pass\r\n";
        string cleaned = Encoding.Latin1.GetString(AuthResultsHeader.RemoveClaimsBy(Encoding.Latin1.GetBytes(raw), "mx.anjal.test"));
        Assert.Equal("Subject: x\r\nAuthentication-Results: mx.google.com; dmarc=pass\r\n\r\nBody line: Authentication-Results: mx.anjal.test; dmarc=pass\r\n", cleaned);
    }

    [Fact]
    public void NothingToRemove_ReturnsTheSameBytes()
    {
        byte[] raw = Encoding.ASCII.GetBytes("Subject: x\r\n\r\nbody\r\n");
        Assert.Same(raw, AuthResultsHeader.RemoveClaimsBy(raw, "mx.anjal.test"));
    }

    private sealed class Capture : IMessageSink
    {
        public byte[] Last { get; private set; } = Array.Empty<byte>();

        public Task<DeliveryResult> DeliverAsync(DeliveryContext ctx, CancellationToken ct = default)
        {
            this.Last = ctx.RawBytes;
            return Task.FromResult(new DeliveryResult { Outcome = DeliveryOutcome.Accepted, ReplyText = "OK" });
        }
    }

    [Fact]
    public async Task TheMta_StripsAForgedVerdict_BeforeDelivery()
    {
        var sink = new Capture();
        var options = new SmtpServerOptions { BindAddress = IPAddress.Loopback, Port = 0, AdvertisedHostName = "mx.anjal.test" };
        using var server = new SmtpServer(options, sink, authenticator: null, enforceReject: false, smtpAuthenticator: null, localDomains: null);
        using var cts = new CancellationTokenSource();
        _ = server.StartAsync(cts.Token);
        await Task.Delay(50);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.BoundPort);
        using NetworkStream stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        async Task<string> Say(string text)
        {
            if (text.Length > 0)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(text));
            }
            string? line;
            do
            {
                line = await reader.ReadLineAsync();
            }
            while (line is not null && line.Length > 3 && line[3] == '-');
            return line ?? string.Empty;
        }
        await Say(string.Empty);
        await Say("EHLO c.test\r\n");
        await Say("MAIL FROM:<boss@hospital.example>\r\n");
        await Say("RCPT TO:<arun@qa.test>\r\n");
        await Say("DATA\r\n");
        await Say("Authentication-Results: mx.anjal.test; spf=pass; dkim=pass; dmarc=pass\r\nFrom: boss@hospital.example\r\nSubject: Urgent\r\n\r\nPay now\r\n.\r\n");
        string delivered = Encoding.ASCII.GetString(sink.Last);
        Assert.StartsWith("Received: from c.test", delivered, StringComparison.Ordinal);
        Assert.DoesNotContain("dmarc=pass", delivered, StringComparison.Ordinal);
        cts.Cancel();
    }
}
