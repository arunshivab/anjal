using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Server.Tests;

/// <summary>
/// Every stage a message passes through must answer a store outage with a
/// temporary failure, so the sender retries. A permanent answer loses mail.
/// </summary>
public class OutageSinkTests
{
    public interface IFullStore : IMessageStore, IMailboxStore
    {
    }

    public class DownStore : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException("connection refused");
    }

    private static IFullStore Down() => System.Reflection.DispatchProxy.Create<IFullStore, DownStore>();

    /// <summary>
    /// The sinks may throw when the store is down; what matters is what the
    /// sending server is told. The SMTP session turns any delivery exception
    /// into 451, so the sender retries. This pins that down end to end with
    /// the real mailbox and routing sinks.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task DuringAStoreOutage_TheSenderIsToldToRetry()
    {
        string root = Path.Combine(Path.GetTempPath(), "anjal-outage-" + Guid.NewGuid().ToString("N"));
        try
        {
            IFullStore store = Down();
            var sink = new CompositeMessageSink(
                new Anjal.Mailbox.MailboxSink(store, new Anjal.Mailbox.MaildirStore(root, "test")),
                new RoutingMessageSink(store, new Anjal.Routing.StoreBackedRoutingTable(store), new NoDispatcher()));
            var options = new SmtpServerOptions { BindAddress = System.Net.IPAddress.Loopback, Port = 0, AdvertisedHostName = "test.localhost" };
            using var server = new SmtpServer(options, sink, authenticator: null, enforceReject: false, smtpAuthenticator: null, localDomains: null);
            using var cts = new System.Threading.CancellationTokenSource();
            _ = server.StartAsync(cts.Token);
            await System.Threading.Tasks.Task.Delay(50);

            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(System.Net.IPAddress.Loopback, server.BoundPort);
            using System.Net.Sockets.NetworkStream stream = tcp.GetStream();
            using var reader = new StreamReader(stream, System.Text.Encoding.ASCII);
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
            async System.Threading.Tasks.Task<string> SendAsync(string text)
            {
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(text));
                return await ReplyAsync();
            }

            await ReplyAsync();
            await SendAsync("EHLO client.test\r\n");
            await SendAsync("MAIL FROM:<s@sender.test>\r\n");
            await SendAsync("RCPT TO:<arun@qa.test>\r\n");
            await SendAsync("DATA\r\n");
            string reply = await SendAsync("From: s@sender.test\r\nSubject: x\r\n\r\nbody\r\n.\r\n");
            Assert.StartsWith("451", reply, StringComparison.Ordinal);
            cts.Cancel();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class NoDispatcher : Anjal.Routing.IWebhookDispatcher
    {
        public System.Threading.Tasks.Task<Anjal.Routing.WebhookDispatchResult> SendAsync(string url, string secret, Anjal.Routing.WebhookPayload payload, System.Threading.CancellationToken ct = default) =>
            throw new InvalidOperationException("not expected");
    }
}
