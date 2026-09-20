using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Anjal.Smtp.Tests;

public class SmtpPolicyHookTests
{
    private sealed class ScriptedPolicy : ISmtpPolicy
    {
        public bool RefuseConnect { get; set; }

        public bool DeferMail { get; set; }

        public bool DeferRcpt { get; set; }

        public bool Throw { get; set; }

        public System.Threading.Tasks.Task<PolicyDecision> OnConnectAsync(string remoteAddress, System.Threading.CancellationToken ct = default)
        {
            if (this.Throw) throw new System.InvalidOperationException("boom");
            return System.Threading.Tasks.Task.FromResult(this.RefuseConnect ? PolicyDecision.Defer("4.7.1 Too many connections", 421) : PolicyDecision.Allow);
        }

        public System.Threading.Tasks.Task<PolicyDecision> OnMailFromAsync(string remoteAddress, string? authenticatedUser, string envelopeFrom, System.Threading.CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult(this.DeferMail ? PolicyDecision.Defer("4.7.1 Rate limited") : PolicyDecision.Allow);

        public System.Threading.Tasks.Task<PolicyDecision> OnRcptToAsync(string remoteAddress, string? authenticatedUser, string envelopeFrom, string recipient, System.Threading.CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult(this.DeferRcpt ? PolicyDecision.Defer("4.7.1 Greylisted, please retry in 300 seconds") : PolicyDecision.Allow);
    }

    private sealed class AcceptSink : IMessageSink
    {
        public System.Threading.Tasks.Task<DeliveryResult> DeliverAsync(DeliveryContext ctx, System.Threading.CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult(new DeliveryResult { Outcome = DeliveryOutcome.Accepted });
    }

    private static async System.Threading.Tasks.Task<(SmtpServer Server, System.Threading.CancellationTokenSource Cts, System.Threading.Tasks.Task Run)> StartAsync(ISmtpPolicy policy)
    {
        var options = new SmtpServerOptions { BindAddress = IPAddress.Loopback, Port = 0, Policy = policy };
        var cts = new System.Threading.CancellationTokenSource();
        var server = new SmtpServer(options, new AcceptSink());
        System.Threading.Tasks.Task run = server.StartAsync(cts.Token);
        await System.Threading.Tasks.Task.Delay(100).ConfigureAwait(false);
        return (server, cts, run);
    }

    private static async System.Threading.Tasks.Task StopAsync((SmtpServer Server, System.Threading.CancellationTokenSource Cts, System.Threading.Tasks.Task Run) s)
    {
        s.Cts.Cancel();
        try { await s.Run.ConfigureAwait(false); }
        catch (System.OperationCanceledException) { }
        s.Server.Dispose();
        s.Cts.Dispose();
    }

    private static async System.Threading.Tasks.Task<(StreamReader Reader, StreamWriter Writer, TcpClient Client)> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
        NetworkStream net = client.GetStream();
        return (new StreamReader(net, Encoding.ASCII), new StreamWriter(net, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true }, client);
    }

    private static async System.Threading.Tasks.Task<string> ReadReplyAsync(StreamReader reader)
    {
        string? line;
        do
        {
            line = await reader.ReadLineAsync().ConfigureAwait(false);
        }
        while (line is not null && line.Length >= 4 && line[3] == '-');
        return line ?? string.Empty;
    }

    [Fact]
    public async System.Threading.Tasks.Task Connect_Refusal_SendsCodeAndCloses()
    {
        var s = await StartAsync(new ScriptedPolicy { RefuseConnect = true });
        try
        {
            (StreamReader reader, StreamWriter _, TcpClient client) = await ConnectAsync(s.Server.BoundPort);
            using (client)
            {
                string banner = await reader.ReadLineAsync() ?? string.Empty;
                Assert.StartsWith("421", banner, System.StringComparison.Ordinal);
                Assert.Null(await reader.ReadLineAsync());
            }
        }
        finally
        {
            await StopAsync(s);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task MailAndRcpt_Deferrals_KeepSessionOpen()
    {
        var policy = new ScriptedPolicy { DeferMail = true };
        var s = await StartAsync(policy);
        try
        {
            (StreamReader reader, StreamWriter writer, TcpClient client) = await ConnectAsync(s.Server.BoundPort);
            using (client)
            {
                await reader.ReadLineAsync();
                await writer.WriteLineAsync("EHLO c");
                await ReadReplyAsync(reader);

                await writer.WriteLineAsync("MAIL FROM:<a@example.com>");
                Assert.StartsWith("451 4.7.1 Rate limited", await reader.ReadLineAsync(), System.StringComparison.Ordinal);

                policy.DeferMail = false;
                policy.DeferRcpt = true;
                await writer.WriteLineAsync("MAIL FROM:<a@example.com>");
                Assert.StartsWith("250", await reader.ReadLineAsync(), System.StringComparison.Ordinal);
                await writer.WriteLineAsync("RCPT TO:<arun@anjal.co.in>");
                Assert.StartsWith("451 4.7.1 Greylisted", await reader.ReadLineAsync(), System.StringComparison.Ordinal);

                policy.DeferRcpt = false;
                await writer.WriteLineAsync("RCPT TO:<arun@anjal.co.in>");
                Assert.StartsWith("250", await reader.ReadLineAsync(), System.StringComparison.Ordinal);
                await writer.WriteLineAsync("QUIT");
                Assert.StartsWith("221", await reader.ReadLineAsync(), System.StringComparison.Ordinal);
            }
        }
        finally
        {
            await StopAsync(s);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task ThrowingPolicy_FailsOpen()
    {
        var s = await StartAsync(new ScriptedPolicy { Throw = true });
        try
        {
            (StreamReader reader, StreamWriter writer, TcpClient client) = await ConnectAsync(s.Server.BoundPort);
            using (client)
            {
                Assert.StartsWith("220", await reader.ReadLineAsync(), System.StringComparison.Ordinal);
                await writer.WriteLineAsync("QUIT");
                Assert.StartsWith("221", await reader.ReadLineAsync(), System.StringComparison.Ordinal);
            }
        }
        finally
        {
            await StopAsync(s);
        }
    }
}
