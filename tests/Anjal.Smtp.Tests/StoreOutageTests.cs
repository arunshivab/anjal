using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Anjal.Smtp.Tests;

/// <summary>
/// DEF-003: when the store behind the local-domain lookup is down, the MTA
/// must defer (4xx) so the sending server retries. It used to answer with a
/// permanent "550 Relaying denied", which makes Gmail and others return the
/// message to its sender - mail lost during any database hiccup.
/// </summary>
public sealed class StoreOutageTests
{
    private sealed class Resolver : ILocalDomainResolver
    {
        public bool Down { get; set; }

        public System.Threading.Tasks.Task<bool> IsLocalAsync(string domain, System.Threading.CancellationToken ct = default)
        {
            if (this.Down)
            {
                throw new InvalidOperationException("connection refused");
            }
            return System.Threading.Tasks.Task.FromResult(string.Equals(domain, "qa.test", StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed class NullSink : IMessageSink
    {
        public System.Threading.Tasks.Task<DeliveryResult> DeliverAsync(DeliveryContext ctx, System.Threading.CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult(new DeliveryResult { Outcome = DeliveryOutcome.Accepted, ReplyText = "OK" });
    }

    private static async System.Threading.Tasks.Task<string> RcptReplyAsync(int port, string recipient)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        using NetworkStream stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        async System.Threading.Tasks.Task<string> ReplyAsync()
        {
            string? line;
            do
            {
                line = await reader.ReadLineAsync();
            }
            while (line is not null && line.Length > 3 && line[3] == '-');
            return line ?? string.Empty;
        }
        async System.Threading.Tasks.Task SendAsync(string text) => await stream.WriteAsync(Encoding.ASCII.GetBytes(text));

        await ReplyAsync();
        await SendAsync("EHLO client.test\r\n");
        await ReplyAsync();
        await SendAsync("MAIL FROM:<s@sender.test>\r\n");
        await ReplyAsync();
        await SendAsync($"RCPT TO:<{recipient}>\r\n");
        return await ReplyAsync();
    }

    [Fact]
    public async System.Threading.Tasks.Task ALookupFailure_Defers_NeverBouncesPermanently()
    {
        var resolver = new Resolver();
        var options = new SmtpServerOptions { BindAddress = IPAddress.Loopback, Port = 0, AdvertisedHostName = "test.localhost" };
        using var server = new SmtpServer(options, new NullSink(), authenticator: null, enforceReject: false, smtpAuthenticator: null, localDomains: resolver);
        using var cts = new System.Threading.CancellationTokenSource();
        _ = server.StartAsync(cts.Token);
        await System.Threading.Tasks.Task.Delay(50);

        // Healthy: local accepted, foreign refused permanently - as before.
        Assert.StartsWith("250", await RcptReplyAsync(server.BoundPort, "arun@qa.test"), StringComparison.Ordinal);
        Assert.StartsWith("550 5.7.1", await RcptReplyAsync(server.BoundPort, "x@elsewhere.test"), StringComparison.Ordinal);

        // Store down: the server cannot know, so it must say "try later".
        resolver.Down = true;
        Assert.StartsWith("451 4.3.0", await RcptReplyAsync(server.BoundPort, "arun@qa.test"), StringComparison.Ordinal);

        // And recovers by itself.
        resolver.Down = false;
        Assert.StartsWith("250", await RcptReplyAsync(server.BoundPort, "arun@qa.test"), StringComparison.Ordinal);
        cts.Cancel();
    }
}
