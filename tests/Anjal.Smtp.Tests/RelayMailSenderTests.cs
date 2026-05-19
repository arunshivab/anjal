namespace Anjal.Smtp.Tests;

public class RelayMailSenderTests
{
    private static readonly string[] ExpectedSingleRecipient = new[] { "to@example" };

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

    private static async System.Threading.Tasks.Task<(SmtpServer server, RecordingSink sink, System.Threading.CancellationTokenSource cts, System.Threading.Tasks.Task task)> StartStubAsync()
    {
        var sink = new RecordingSink();
        var opts = new SmtpServerOptions
        {
            BindAddress = System.Net.IPAddress.Loopback,
            Port = 0,
            AdvertisedHostName = "stub.test",
        };
        var server = new SmtpServer(opts, sink);
        var cts = new System.Threading.CancellationTokenSource();
        System.Threading.Tasks.Task t = server.StartAsync(cts.Token);
        await System.Threading.Tasks.Task.Delay(50);
        return (server, sink, cts, t);
    }

    private static async System.Threading.Tasks.Task StopAsync(System.Threading.CancellationTokenSource cts, SmtpServer server, System.Threading.Tasks.Task t)
    {
        cts.Cancel();
        server.Dispose();
        try { await t; } catch (System.OperationCanceledException) { }
        cts.Dispose();
    }

    [Fact]
    public async System.Threading.Tasks.Task Send_StubAccepts_ReturnsSent()
    {
        var (server, sink, cts, task) = await StartStubAsync();
        try
        {
            var sender = new RelayMailSender(new RelayOptions
            {
                Host = "127.0.0.1",
                Port = server.BoundPort,
                ClientHostName = "anjal.test",
                ConnectTimeout = System.TimeSpan.FromSeconds(5),
            });

            SendResult result = await sender.SendAsync(new OutboundDelivery
            {
                EnvelopeFrom = "from@example",
                EnvelopeTo = ExpectedSingleRecipient,
                RawBytes = System.Text.Encoding.UTF8.GetBytes("Subject: Test\r\n\r\nBody.\r\n"),
            });

            Assert.Equal(SendOutcome.Sent, result.Outcome);
            Assert.Equal(250, result.ReplyCode);
            Assert.Single(sink.Received);
            Assert.Equal("from@example", sink.Received[0].EnvelopeFrom);
            Assert.Equal(ExpectedSingleRecipient, sink.Received[0].EnvelopeTo);
        }
        finally
        {
            await StopAsync(cts, server, task);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Send_StubRejectsPermanent_ReturnsPermanentFailure()
    {
        var (server, sink, cts, task) = await StartStubAsync();
        try
        {
            sink.NextOutcome = DeliveryOutcome.PermanentFailure;
            sink.NextReply = "Mailbox unavailable";

            var sender = new RelayMailSender(new RelayOptions
            {
                Host = "127.0.0.1",
                Port = server.BoundPort,
                ClientHostName = "anjal.test",
                ConnectTimeout = System.TimeSpan.FromSeconds(5),
            });

            SendResult result = await sender.SendAsync(new OutboundDelivery
            {
                EnvelopeFrom = "from@example",
                EnvelopeTo = ExpectedSingleRecipient,
                RawBytes = System.Text.Encoding.UTF8.GetBytes("Subject: Test\r\n\r\nBody.\r\n"),
            });

            Assert.Equal(SendOutcome.PermanentFailure, result.Outcome);
            Assert.Equal(550, result.ReplyCode);
        }
        finally
        {
            await StopAsync(cts, server, task);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Send_StubRejectsTransient_ReturnsTransientFailure()
    {
        var (server, sink, cts, task) = await StartStubAsync();
        try
        {
            sink.NextOutcome = DeliveryOutcome.TransientFailure;
            sink.NextReply = "Try later";

            var sender = new RelayMailSender(new RelayOptions
            {
                Host = "127.0.0.1",
                Port = server.BoundPort,
                ClientHostName = "anjal.test",
                ConnectTimeout = System.TimeSpan.FromSeconds(5),
            });

            SendResult result = await sender.SendAsync(new OutboundDelivery
            {
                EnvelopeFrom = "from@example",
                EnvelopeTo = ExpectedSingleRecipient,
                RawBytes = System.Text.Encoding.UTF8.GetBytes("Subject: Test\r\n\r\nBody.\r\n"),
            });

            Assert.Equal(SendOutcome.TransientFailure, result.Outcome);
            Assert.Equal(451, result.ReplyCode);
        }
        finally
        {
            await StopAsync(cts, server, task);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Send_UnreachableHost_ReturnsTransientFailure()
    {
        // Connect to a port that has nothing listening - should be transient.
        var sender = new RelayMailSender(new RelayOptions
        {
            Host = "127.0.0.1",
            Port = 1,  // Unbound on every system I've seen.
            ClientHostName = "anjal.test",
            ConnectTimeout = System.TimeSpan.FromSeconds(2),
        });

        SendResult result = await sender.SendAsync(new OutboundDelivery
        {
            EnvelopeFrom = "from@example",
            EnvelopeTo = ExpectedSingleRecipient,
            RawBytes = System.Text.Encoding.UTF8.GetBytes("Subject: Test\r\n\r\nBody.\r\n"),
        });

        Assert.Equal(SendOutcome.TransientFailure, result.Outcome);
    }

    [Fact]
    public async System.Threading.Tasks.Task Send_NoRecipients_ReturnsPermanentFailure()
    {
        var sender = new RelayMailSender(new RelayOptions
        {
            Host = "127.0.0.1",
            Port = 25,
        });

        SendResult result = await sender.SendAsync(new OutboundDelivery
        {
            EnvelopeFrom = "from@example",
            EnvelopeTo = System.Array.Empty<string>(),
            RawBytes = System.Array.Empty<byte>(),
        });

        Assert.Equal(SendOutcome.PermanentFailure, result.Outcome);
    }

    [Fact]
    public void DotStuff_LeadingDotDoubled()
    {
        byte[] input = System.Text.Encoding.ASCII.GetBytes("hello\r\n.dotline\r\nbye\r\n");
        byte[] stuffed = SmtpClientSession.DotStuff(input);
        string s = System.Text.Encoding.ASCII.GetString(stuffed);
        Assert.Contains("\r\n..dotline\r\n", s, System.StringComparison.Ordinal);
        Assert.DoesNotContain("\r\n.dotline\r\n", s, System.StringComparison.Ordinal);
    }

    [Fact]
    public void DotStuff_NonDotLines_Untouched()
    {
        byte[] input = System.Text.Encoding.ASCII.GetBytes("hello\r\nworld\r\n");
        byte[] stuffed = SmtpClientSession.DotStuff(input);
        Assert.Equal(input, stuffed);
    }

    [Fact]
    public void ClassifyReply_5xx_Permanent()
    {
        SendResult r = RelayMailSender.ClassifyReply(new SmtpReply { Code = 550, Text = "no such user" }, "RCPT");
        Assert.Equal(SendOutcome.PermanentFailure, r.Outcome);
        Assert.Equal(550, r.ReplyCode);
    }

    [Fact]
    public void ClassifyReply_4xx_Transient()
    {
        SendResult r = RelayMailSender.ClassifyReply(new SmtpReply { Code = 451, Text = "try later" }, "DATA");
        Assert.Equal(SendOutcome.TransientFailure, r.Outcome);
        Assert.Equal(451, r.ReplyCode);
    }
}
