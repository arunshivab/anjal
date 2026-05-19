namespace Anjal.Smtp.Tests;

public class SmtpServerTests
{
    private static SmtpServerOptions LocalhostEphemeralOptions() => new()
    {
        BindAddress = System.Net.IPAddress.Loopback,
        Port = 0,
        AdvertisedHostName = "anjal.test",
    };

    private sealed class RecordingSink : IMessageSink
    {
        public System.Collections.Generic.List<DeliveryContext> Received { get; } = new();
        public DeliveryOutcome NextOutcome { get; set; } = DeliveryOutcome.Accepted;
        public string NextReply { get; set; } = "OK";

        public System.Threading.Tasks.Task<DeliveryResult> DeliverAsync(DeliveryContext ctx, System.Threading.CancellationToken ct = default)
        {
            this.Received.Add(ctx);
            return System.Threading.Tasks.Task.FromResult(new DeliveryResult
            {
                Outcome = this.NextOutcome,
                ReplyText = this.NextReply,
            });
        }
    }

    private sealed class SmtpClient : System.IDisposable
    {
        private readonly System.Net.Sockets.TcpClient client;
        private readonly System.IO.StreamReader reader;
        private readonly System.IO.StreamWriter writer;

        private SmtpClient(System.Net.Sockets.TcpClient c, System.IO.StreamReader r, System.IO.StreamWriter w)
        {
            this.client = c;
            this.reader = r;
            this.writer = w;
        }

        public static async System.Threading.Tasks.Task<SmtpClient> ConnectAsync(int port)
        {
            var c = new System.Net.Sockets.TcpClient();
            await c.ConnectAsync(System.Net.IPAddress.Loopback, port);
            var stream = c.GetStream();
            var r = new System.IO.StreamReader(stream, System.Text.Encoding.ASCII);
            var w = new System.IO.StreamWriter(stream, System.Text.Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            return new SmtpClient(c, r, w);
        }

        public async System.Threading.Tasks.Task<string> ReadReplyAsync()
        {
            var sb = new System.Text.StringBuilder();
            while (true)
            {
                string? line = await this.reader.ReadLineAsync();
                if (line is null)
                {
                    return sb.ToString();
                }
                sb.AppendLine(line);
                if (line.Length >= 4 && line[3] == ' ')
                {
                    return sb.ToString();
                }
            }
        }

        public async System.Threading.Tasks.Task<string> SendAsync(string cmd)
        {
            await this.writer.WriteLineAsync(cmd);
            return await this.ReadReplyAsync();
        }

        public async System.Threading.Tasks.Task WriteRawAsync(string text)
        {
            await this.writer.WriteAsync(text);
            await this.writer.FlushAsync();
        }

        public void Dispose() => this.client.Dispose();
    }

    private static async System.Threading.Tasks.Task<(SmtpServer server, RecordingSink sink, System.Threading.CancellationTokenSource cts, System.Threading.Tasks.Task serverTask)> StartServerAsync(SmtpServerOptions? opts = null)
    {
        var sink = new RecordingSink();
        var server = new SmtpServer(opts ?? LocalhostEphemeralOptions(), sink);
        var cts = new System.Threading.CancellationTokenSource();
        System.Threading.Tasks.Task serverTask = server.StartAsync(cts.Token);
        await System.Threading.Tasks.Task.Delay(50);
        return (server, sink, cts, serverTask);
    }

    private static async System.Threading.Tasks.Task ShutdownAsync(System.Threading.CancellationTokenSource cts, SmtpServer server, System.Threading.Tasks.Task serverTask)
    {
        cts.Cancel();
        server.Dispose();
        try
        {
            await serverTask;
        }
        catch (System.OperationCanceledException)
        {
            // Expected.
        }
        cts.Dispose();
    }

    [Fact]
    public async System.Threading.Tasks.Task Greeting_StartsWith220()
    {
        var (server, _, cts, task) = await StartServerAsync();
        try
        {
            using SmtpClient c = await SmtpClient.ConnectAsync(server.BoundPort);
            string greeting = await c.ReadReplyAsync();
            Assert.StartsWith("220 anjal.test", greeting, System.StringComparison.Ordinal);
        }
        finally
        {
            await ShutdownAsync(cts, server, task);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Ehlo_AdvertisesCapabilities()
    {
        var (server, _, cts, task) = await StartServerAsync();
        try
        {
            using SmtpClient c = await SmtpClient.ConnectAsync(server.BoundPort);
            await c.ReadReplyAsync();
            string reply = await c.SendAsync("EHLO test.local");

            Assert.Contains("250", reply, System.StringComparison.Ordinal);
            Assert.Contains("SIZE", reply, System.StringComparison.Ordinal);
            Assert.Contains("8BITMIME", reply, System.StringComparison.Ordinal);
            Assert.Contains("HELP", reply, System.StringComparison.Ordinal);
        }
        finally
        {
            await ShutdownAsync(cts, server, task);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task MailBeforeHelo_Returns503()
    {
        var (server, _, cts, task) = await StartServerAsync();
        try
        {
            using SmtpClient c = await SmtpClient.ConnectAsync(server.BoundPort);
            await c.ReadReplyAsync();
            string reply = await c.SendAsync("MAIL FROM:<a@b>");
            Assert.StartsWith("503", reply, System.StringComparison.Ordinal);
        }
        finally
        {
            await ShutdownAsync(cts, server, task);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task FullTransaction_AcceptedAndDelivered()
    {
        var (server, sink, cts, task) = await StartServerAsync();
        try
        {
            using SmtpClient c = await SmtpClient.ConnectAsync(server.BoundPort);
            await c.ReadReplyAsync();
            await c.SendAsync("EHLO test.local");
            string mailReply = await c.SendAsync("MAIL FROM:<from@example>");
            Assert.StartsWith("250", mailReply, System.StringComparison.Ordinal);
            string rcptReply = await c.SendAsync("RCPT TO:<to@example>");
            Assert.StartsWith("250", rcptReply, System.StringComparison.Ordinal);
            string dataReply = await c.SendAsync("DATA");
            Assert.StartsWith("354", dataReply, System.StringComparison.Ordinal);

            await c.WriteRawAsync("Subject: Test\r\n\r\nBody here.\r\n.\r\n");
            string accept = await c.ReadReplyAsync();
            Assert.StartsWith("250", accept, System.StringComparison.Ordinal);

            await c.SendAsync("QUIT");
        }
        finally
        {
            await ShutdownAsync(cts, server, task);
        }

        Assert.Single(sink.Received);
        DeliveryContext ctx = sink.Received[0];
        Assert.Equal("from@example", ctx.EnvelopeFrom);
        Assert.Single(ctx.EnvelopeTo);
        Assert.Equal("to@example", ctx.EnvelopeTo[0]);
        Assert.Contains("Body here.", System.Text.Encoding.UTF8.GetString(ctx.RawBytes), System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task DotStuffing_LeadingDotInBodyUnstuffed()
    {
        var (server, sink, cts, task) = await StartServerAsync();
        try
        {
            using SmtpClient c = await SmtpClient.ConnectAsync(server.BoundPort);
            await c.ReadReplyAsync();
            await c.SendAsync("EHLO test.local");
            await c.SendAsync("MAIL FROM:<from@example>");
            await c.SendAsync("RCPT TO:<to@example>");
            await c.SendAsync("DATA");

            // Body has a line starting with a dot - sender stuffs an extra dot.
            // Server should produce a body where the line starts with just one dot.
            await c.WriteRawAsync("Subject: Test\r\n\r\nFirst line\r\n..Dotline\r\n.\r\n");
            await c.ReadReplyAsync();
            await c.SendAsync("QUIT");
        }
        finally
        {
            await ShutdownAsync(cts, server, task);
        }

        string body = System.Text.Encoding.UTF8.GetString(sink.Received[0].RawBytes);
        Assert.Contains("\r\n.Dotline\r\n", body, System.StringComparison.Ordinal);
        Assert.DoesNotContain("..Dotline", body, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task DeliveryTransientFailure_Maps451()
    {
        var (server, sink, cts, task) = await StartServerAsync();
        try
        {
            sink.NextOutcome = DeliveryOutcome.TransientFailure;
            sink.NextReply = "Try again later";

            using SmtpClient c = await SmtpClient.ConnectAsync(server.BoundPort);
            await c.ReadReplyAsync();
            await c.SendAsync("EHLO test.local");
            await c.SendAsync("MAIL FROM:<from@example>");
            await c.SendAsync("RCPT TO:<to@example>");
            await c.SendAsync("DATA");
            await c.WriteRawAsync("Subject: Test\r\n\r\nBody.\r\n.\r\n");
            string reply = await c.ReadReplyAsync();

            Assert.StartsWith("451", reply, System.StringComparison.Ordinal);
        }
        finally
        {
            await ShutdownAsync(cts, server, task);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task DeliveryPermanentFailure_Maps550()
    {
        var (server, sink, cts, task) = await StartServerAsync();
        try
        {
            sink.NextOutcome = DeliveryOutcome.PermanentFailure;
            sink.NextReply = "No such user here";

            using SmtpClient c = await SmtpClient.ConnectAsync(server.BoundPort);
            await c.ReadReplyAsync();
            await c.SendAsync("EHLO test.local");
            await c.SendAsync("MAIL FROM:<from@example>");
            await c.SendAsync("RCPT TO:<to@example>");
            await c.SendAsync("DATA");
            await c.WriteRawAsync("Subject: Test\r\n\r\nBody.\r\n.\r\n");
            string reply = await c.ReadReplyAsync();

            Assert.StartsWith("550", reply, System.StringComparison.Ordinal);
        }
        finally
        {
            await ShutdownAsync(cts, server, task);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Rset_ClearsTransaction()
    {
        var (server, _, cts, task) = await StartServerAsync();
        try
        {
            using SmtpClient c = await SmtpClient.ConnectAsync(server.BoundPort);
            await c.ReadReplyAsync();
            await c.SendAsync("EHLO test.local");
            await c.SendAsync("MAIL FROM:<from@example>");
            string rsetReply = await c.SendAsync("RSET");
            Assert.StartsWith("250", rsetReply, System.StringComparison.Ordinal);
            // After RSET, MAIL FROM should be required again before RCPT.
            string rcptReply = await c.SendAsync("RCPT TO:<to@example>");
            Assert.StartsWith("503", rcptReply, System.StringComparison.Ordinal);
        }
        finally
        {
            await ShutdownAsync(cts, server, task);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Quit_Returns221AndCloses()
    {
        var (server, _, cts, task) = await StartServerAsync();
        try
        {
            using SmtpClient c = await SmtpClient.ConnectAsync(server.BoundPort);
            await c.ReadReplyAsync();
            string reply = await c.SendAsync("QUIT");
            Assert.StartsWith("221", reply, System.StringComparison.Ordinal);
        }
        finally
        {
            await ShutdownAsync(cts, server, task);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Noop_Returns250()
    {
        var (server, _, cts, task) = await StartServerAsync();
        try
        {
            using SmtpClient c = await SmtpClient.ConnectAsync(server.BoundPort);
            await c.ReadReplyAsync();
            string reply = await c.SendAsync("NOOP");
            Assert.StartsWith("250", reply, System.StringComparison.Ordinal);
        }
        finally
        {
            await ShutdownAsync(cts, server, task);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task UnknownCommand_Returns500()
    {
        var (server, _, cts, task) = await StartServerAsync();
        try
        {
            using SmtpClient c = await SmtpClient.ConnectAsync(server.BoundPort);
            await c.ReadReplyAsync();
            string reply = await c.SendAsync("ZZZZ");
            Assert.StartsWith("500", reply, System.StringComparison.Ordinal);
        }
        finally
        {
            await ShutdownAsync(cts, server, task);
        }
    }
}
