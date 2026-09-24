using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Anjal.Smtp.Tests;

/// <summary>DEF-042: an unknown recipient is refused at RCPT TO, before the message is transferred.</summary>
public sealed class UnknownRecipientTests
{
    private sealed class Domains : ILocalDomainResolver
    {
        public Task<bool> IsLocalAsync(string domain, CancellationToken ct = default) =>
            Task.FromResult(string.Equals(domain, "qa.test", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class Recipients : IRecipientResolver
    {
        public Func<string, bool?> Answer { get; set; } = _ => true;

        public Task<bool?> ExistsAsync(string address, CancellationToken ct = default) => Task.FromResult(this.Answer(address));
    }

    private sealed class CountingSink : IMessageSink
    {
        public int Delivered { get; private set; }

        public Task<DeliveryResult> DeliverAsync(DeliveryContext ctx, CancellationToken ct = default)
        {
            this.Delivered++;
            return Task.FromResult(new DeliveryResult { Outcome = DeliveryOutcome.Accepted, ReplyText = "OK" });
        }
    }

    private static async Task<(string RcptReply, CountingSink Sink)> SendAsync(Recipients recipients, string rcpt)
    {
        var sink = new CountingSink();
        var options = new SmtpServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            AdvertisedHostName = "mx.test",
            Recipients = recipients,
        };
        using var server = new SmtpServer(options, sink, authenticator: null, enforceReject: false, smtpAuthenticator: null, localDomains: new Domains());
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
        await Say("MAIL FROM:<s@sender.test>\r\n");
        string reply = await Say($"RCPT TO:<{rcpt}>\r\n");
        cts.Cancel();
        return (reply, sink);
    }

    [Fact]
    public async Task AnUnknownMailbox_IsRefusedAtRcpt_AndNoMessageIsTransferred()
    {
        var recipients = new Recipients { Answer = _ => false };
        (string reply, CountingSink sink) = await SendAsync(recipients, "arnu@qa.test");
        Assert.StartsWith("550 5.1.1", reply, StringComparison.Ordinal);
        Assert.Equal(0, sink.Delivered);
    }

    [Fact]
    public async Task AKnownMailbox_IsAccepted()
    {
        (string reply, _) = await SendAsync(new Recipients { Answer = _ => true }, "arun@qa.test");
        Assert.StartsWith("250", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenItCannotBeDetermined_TheRecipientIsAccepted_NotRefused()
    {
        // A store that cannot answer must never cause a bounce (DEF-003).
        (string reply, _) = await SendAsync(new Recipients { Answer = _ => null }, "arun@qa.test");
        Assert.StartsWith("250", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheCheckThrows_TheRecipientIsAccepted()
    {
        var recipients = new Recipients { Answer = _ => throw new InvalidOperationException("connection refused") };
        (string reply, _) = await SendAsync(recipients, "arun@qa.test");
        Assert.StartsWith("250", reply, StringComparison.Ordinal);
    }
}
