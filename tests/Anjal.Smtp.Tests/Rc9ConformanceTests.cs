using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Anjal.Smtp.Tests;

/// <summary>
/// v1.0.0-rc.9 conformance (ANJAL-TST-12): the audit's probes as permanent
/// tests. Each asserts the standard's behaviour, and each failed on rc.8.
/// DEF-070 (555 for unadvertised parameters), DEF-072 (5-minute idle timeout),
/// DEF-074 (EXPN 502; PIPELINING and ENHANCEDSTATUSCODES advertised; an
/// enhanced status code on every reply).
/// </summary>
public sealed partial class Rc9ConformanceTests : System.IDisposable
{
    private readonly SmtpServer server;

    public Rc9ConformanceTests()
    {
        var options = new SmtpServerOptions { BindAddress = IPAddress.Loopback, Port = 0, AdvertisedHostName = "mail.anjal.test" };
        this.server = new SmtpServer(options, new AcceptAll());
        _ = this.server.StartAsync(System.Threading.CancellationToken.None);
    }

    public void Dispose() => this.server.Dispose();

    private sealed class AcceptAll : IMessageSink
    {
        public Task<DeliveryResult> DeliverAsync(DeliveryContext ctx, CancellationToken ct = default) =>
            Task.FromResult(new DeliveryResult { Outcome = DeliveryOutcome.Accepted, ReplyText = "Delivered to 1 mailbox(es)" });
    }

    [GeneratedRegex(@"^[245]\d\d[ -][245]\.\d{1,3}\.\d{1,3}(\s|$)")]
    private static partial Regex EnhancedReply();

    private sealed class Client : System.IDisposable
    {
        private readonly TcpClient tcp = new();
        private NetworkStream net = null!;
        private System.IO.StreamReader reader = null!;

        public static async Task<Client> OpenAsync(int port)
        {
            var c = new Client();
            await c.tcp.ConnectAsync(IPAddress.Loopback, port);
            c.net = c.tcp.GetStream();
            c.reader = new System.IO.StreamReader(c.net, Encoding.ASCII);
            await c.ReplyAsync();
            return c;
        }

        public async Task<List<string>> ReplyAsync()
        {
            var lines = new List<string>();
            string line;
            do
            {
                line = (await this.reader.ReadLineAsync())!;
                lines.Add(line);
            }
            while (line.Length > 3 && line[3] == '-');
            return lines;
        }

        public async Task<List<string>> CmdAsync(string command)
        {
            await this.net.WriteAsync(Encoding.ASCII.GetBytes(command + "\r\n"));
            return await this.ReplyAsync();
        }

        public void Dispose() => this.tcp.Dispose();
    }

    [Fact]
    public async Task Ehlo_AdvertisesPipeliningAndEnhancedStatusCodes()
    {
        using Client c = await Client.OpenAsync(this.server.BoundPort);
        List<string> ehlo = await c.CmdAsync("EHLO client.example.net");
        Assert.Contains("250-PIPELINING", ehlo);
        Assert.Contains("250-ENHANCEDSTATUSCODES", ehlo);
    }

    [Fact]
    public async Task EveryReply_OtherThanTheGreetingAndEhlo_CarriesAnEnhancedStatusCode()
    {
        using Client c = await Client.OpenAsync(this.server.BoundPort);
        var replies = new List<string>();
        async Task Run(string cmd)
        {
            foreach (string l in await c.CmdAsync(cmd))
            {
                replies.Add(l);
            }
        }
        await Run("MAIL FROM:<a@example.net>");                 // before EHLO: 503
        await c.CmdAsync("EHLO client.example.net");
        foreach (string cmd in new[] { "NOOP", "RSET", "VRFY someone", "EXPN staff", "HELP", "FROBNICATE", "RCPT TO:<x@example.net>", "DATA",
                                       "MAIL FROM:x", "MAIL FROM:<a@example.net> FOO=BAR", "MAIL FROM:<a@example.net>", "RCPT TO:y",
                                       "RCPT TO:<x@example.net> NOTIFY=NEVER", "RCPT TO:<x@example.net>", "STARTTLS", "AUTH PLAIN" })
        {
            await Run(cmd);
        }
        await Run("DATA");
        foreach (string l in await c.CmdAsync("Subject: t\r\n\r\nbody\r\n."))
        {
            replies.Add(l);
        }
        await Run("QUIT");
        Assert.All(replies.Where(r => r[0] != '3'), r => Assert.Matches(EnhancedReply(), r));
        Assert.Contains(replies, r => r.StartsWith("250 2.0.0 Delivered", System.StringComparison.Ordinal));
        Assert.Contains(replies, r => r.StartsWith("221 2.0.0", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task Expn_IsRecognisedButNotImplemented_502()
    {
        using Client c = await Client.OpenAsync(this.server.BoundPort);
        await c.CmdAsync("EHLO client.example.net");
        Assert.StartsWith("502 5.5.1", (await c.CmdAsync("EXPN staff"))[0], System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MAIL FROM:<a@example.net> FOO=BAR", "555 5.5.4")]
    [InlineData("MAIL FROM:<a@example.net> RET=HDRS", "555 5.5.4")]
    [InlineData("MAIL FROM:<a@example.net> ENVID=x", "555 5.5.4")]
    [InlineData("MAIL FROM:<a@example.net> SMTPUTF8", "555 5.5.4")]
    [InlineData("MAIL FROM:<a@example.net> AUTH=<>", "555 5.5.4")]
    [InlineData("MAIL FROM:<a@example.net> BODY=BINARYMIME", "501 5.5.4")]
    [InlineData("MAIL FROM:<a@example.net> SIZE=abc", "501 5.5.4")]
    [InlineData("MAIL FROM:<a@example.net> SIZE=1000 BODY=8BITMIME", "250 2.1.0")]
    [InlineData("mail from:<a@example.net> body=7bit", "250 2.1.0")]
    public async Task MailParameters_OnlyAdvertisedOnesAreAccepted(string command, string expected)
    {
        using Client c = await Client.OpenAsync(this.server.BoundPort);
        await c.CmdAsync("EHLO client.example.net");
        Assert.StartsWith(expected, (await c.CmdAsync(command))[0], System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task RcptParameters_AreRefused_WhileAPlainRcptIsAccepted()
    {
        using Client c = await Client.OpenAsync(this.server.BoundPort);
        await c.CmdAsync("EHLO client.example.net");
        await c.CmdAsync("MAIL FROM:<a@example.net>");
        Assert.StartsWith("555 5.5.4", (await c.CmdAsync("RCPT TO:<x@example.net> NOTIFY=NEVER"))[0], System.StringComparison.Ordinal);
        Assert.StartsWith("250 2.1.5", (await c.CmdAsync("RCPT TO:<x@example.net>"))[0], System.StringComparison.Ordinal);
    }

    [Fact]
    public void IdleTimeout_DefaultsToFiveMinutes()
    {
        Assert.Equal(System.TimeSpan.FromMinutes(5), new SmtpServerOptions().CommandTimeout);
    }
}
