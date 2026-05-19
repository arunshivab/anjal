using System.Net;
using System.Net.Sockets;
using System.Text;
using Anjal.Mime;
using Anjal.Routing;
using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Examples.InboundEndToEnd;

/// <summary>
/// Demonstrates the full inbound pipeline on localhost. No PostgreSQL or
/// external network needed - everything runs in-process:
///
///   1. Start an in-memory <see cref="InMemoryMessageStore"/>.
///   2. Register a routing rule and a tag grant for case "CASE-18472".
///   3. Start an <see cref="HttpListener"/> on port 0 as the stub webhook.
///   4. Start an <see cref="SmtpServer"/> on port 0.
///   5. Open a TCP client and replay an SMTP transaction with a multipart
///      message addressed to <c>reports+CASE-18472@anjal.localhost</c>.
///   6. Watch the message flow through: parsed, stored, webhook fired.
///   7. Print the stored row and the webhook delivery for inspection.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        // --- 1. Store and routing ---
        var store = new InMemoryMessageStore();
        await store.UpsertRoutingRuleAsync(new RoutingRule
        {
            LocalPart = "reports",
            WebhookUrl = "http://placeholder",
            WebhookSecret = "00112233445566778899aabbccddeeff",
        });
        await store.CreateTagGrantAsync(new TagGrant
        {
            LocalPart = "reports",
            Tag = "CASE-18472",
            CorrelationKey = "case:18472",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });

        // --- 2. Stub HTTP webhook listener ---
        var listener = new HttpListener();
        int webhookPort = GetFreePort();
        string webhookUrl = $"http://127.0.0.1:{webhookPort}/inbound";
        listener.Prefixes.Add($"http://127.0.0.1:{webhookPort}/");
        listener.Start();
        Console.WriteLine($"Webhook stub listening at {webhookUrl}");

        // Update the rule with the real URL now that we have a port.
        await store.UpsertRoutingRuleAsync(new RoutingRule
        {
            LocalPart = "reports",
            WebhookUrl = webhookUrl,
            WebhookSecret = "00112233445566778899aabbccddeeff",
        });

        var webhookReceived = new TaskCompletionSource<(int status, string body, string signature)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            HttpListenerContext ctx = await listener.GetContextAsync();
            using var reader = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            string body = await reader.ReadToEndAsync();
            string sig = ctx.Request.Headers["X-Anjal-Signature"] ?? "";
            ctx.Response.StatusCode = 200;
            ctx.Response.OutputStream.Close();
            webhookReceived.SetResult((200, body, sig));
        });

        // --- 3. SMTP server ---
        var routing = new StoreBackedRoutingTable(store);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var dispatcher = new HttpWebhookDispatcher(http);

        void Log(string line) => Console.WriteLine($"[server] {line}");
        var sink = new RoutingMessageSink(store, routing, dispatcher, Log);

        var options = new SmtpServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            AdvertisedHostName = "anjal.localhost",
        };
        using var cts = new CancellationTokenSource();
        using var server = new SmtpServer(options, sink);
        Task serverTask = server.StartAsync(cts.Token);
        await Task.Delay(100);  // Brief delay for the listener to bind.
        Console.WriteLine($"SMTP server listening on 127.0.0.1:{server.BoundPort}");

        // --- 4. Replay an SMTP transaction ---
        await ReplayTransactionAsync(server.BoundPort);

        // --- 5. Wait for the webhook to fire ---
        Task delay = Task.Delay(TimeSpan.FromSeconds(3));
        Task finished = await Task.WhenAny(webhookReceived.Task, delay);
        if (finished == delay)
        {
            Console.WriteLine("ERROR: webhook did not fire within 3 seconds.");
            cts.Cancel();
            return 1;
        }

        (int status, string body, string signature) = await webhookReceived.Task;
        Console.WriteLine();
        Console.WriteLine("=== Webhook received ===");
        Console.WriteLine($"  status:    {status}");
        Console.WriteLine($"  signature: {signature}");
        Console.WriteLine($"  body:      {Truncate(body, 200)}");

        // --- 6. Inspect the store ---
        Console.WriteLine();
        Console.WriteLine("=== Store contents ===");
        Console.WriteLine($"  routing rules:        {store.Rules.Count}");
        Console.WriteLine($"  inbound messages:     {store.Messages.Count}");
        Console.WriteLine($"  webhook deliveries:   {store.Deliveries.Count}");
        if (store.Messages.Count > 0)
        {
            InboundMessage stored = store.Messages[0];
            Console.WriteLine();
            Console.WriteLine("  First stored message:");
            Console.WriteLine($"    id:           {stored.Id}");
            Console.WriteLine($"    envelope_to:  {stored.EnvelopeTo}");
            Console.WriteLine($"    local_part:   {stored.LocalPart}");
            Console.WriteLine($"    tag:          {stored.Tag}");
            Console.WriteLine($"    subject:      {stored.Subject}");
            Console.WriteLine($"    raw bytes:    {stored.RawBytes.Length}");
        }
        if (store.Deliveries.Count > 0)
        {
            WebhookDelivery d = store.Deliveries[0];
            Console.WriteLine();
            Console.WriteLine("  First webhook delivery:");
            Console.WriteLine($"    url:          {d.Url}");
            Console.WriteLine($"    status_code:  {d.StatusCode}");
            Console.WriteLine($"    error:        {(string.IsNullOrEmpty(d.ErrorMessage) ? "(none)" : d.ErrorMessage)}");
        }

        // --- 7. Cleanup ---
        cts.Cancel();
        listener.Stop();
        try
        {
            await serverTask;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        Console.WriteLine();
        Console.WriteLine("End-to-end demo complete.");
        return 0;
    }

    private static async Task ReplayTransactionAsync(int port)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var net = client.GetStream();
        var reader = new System.IO.StreamReader(net, Encoding.ASCII);
        var writer = new System.IO.StreamWriter(net, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

        async Task<string> ReadReplyAsync()
        {
            var sb = new StringBuilder();
            while (true)
            {
                string? line = await reader.ReadLineAsync();
                if (line is null)
                {
                    return sb.ToString();
                }
                sb.AppendLine(line);
                // Multi-line replies have "-" in column 4; final line has " ".
                if (line.Length >= 4 && line[3] == ' ')
                {
                    return sb.ToString();
                }
            }
        }

        async Task SendAsync(string cmd, string? expected = null)
        {
            await writer.WriteLineAsync(cmd);
            string reply = await ReadReplyAsync();
            Console.Write($"[client] > {cmd}\n[client] < {reply}");
            if (expected is not null && !reply.StartsWith(expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Expected reply starting with '{expected}', got: {reply}");
            }
        }

        // Banner first.
        string banner = await ReadReplyAsync();
        Console.Write($"[client] < {banner}");

        await SendAsync("EHLO test.client", "250");
        await SendAsync("MAIL FROM:<patient@gmail.com>", "250");
        await SendAsync("RCPT TO:<reports+CASE-18472@anjal.localhost>", "250");
        await SendAsync("DATA", "354");

        string body =
            "From: Patient <patient@gmail.com>\r\n" +
            "To: Anjal <reports+CASE-18472@anjal.localhost>\r\n" +
            "Subject: Lab report for case 18472\r\n" +
            "Message-ID: <example-001@gmail.com>\r\n" +
            "Content-Type: multipart/mixed; boundary=X\r\n" +
            "\r\n" +
            "--X\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "\r\n" +
            "Hello, please find my lab report attached.\r\n" +
            "--X\r\n" +
            "Content-Type: application/pdf; name=\"report.pdf\"\r\n" +
            "Content-Transfer-Encoding: base64\r\n" +
            "\r\n" +
            "SGVsbG8gV29ybGQ=\r\n" +
            "--X--\r\n" +
            ".";
        await writer.WriteAsync(body + "\r\n");
        await writer.FlushAsync();
        string reply = await ReadReplyAsync();
        Console.Write($"[client] < {reply}");

        await SendAsync("QUIT", "221");
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "...");
}

/// <summary>
/// Minimal copy of the production <see cref="Anjal.Server.RoutingMessageSink"/>
/// pattern so the example doesn't need to reference the Anjal.Server project
/// (which depends on Npgsql via its PostgreSQL store).
/// </summary>
internal sealed class RoutingMessageSink : IMessageSink
{
    private readonly IMessageStore store;
    private readonly IRoutingTable routing;
    private readonly IWebhookDispatcher dispatcher;
    private readonly Action<string>? log;

    public RoutingMessageSink(
        IMessageStore store,
        IRoutingTable routing,
        IWebhookDispatcher dispatcher,
        Action<string>? log = null)
    {
        this.store = store;
        this.routing = routing;
        this.dispatcher = dispatcher;
        this.log = log;
    }

    public async Task<DeliveryResult> DeliverAsync(DeliveryContext ctx, CancellationToken ct = default)
    {
        if (ctx.EnvelopeTo.Count == 0)
        {
            return new DeliveryResult { Outcome = DeliveryOutcome.PermanentFailure, ReplyText = "No recipients" };
        }

        MimeMessage parsed = MimeParser.Parse(ctx.RawBytes);
        int delivered = 0;
        foreach (string rcpt in ctx.EnvelopeTo)
        {
            RoutingDecision decision = await this.routing.ResolveAsync(rcpt, ct);
            if (decision.Outcome != RoutingOutcome.Accepted || decision.Rule is null)
            {
                this.log?.Invoke($"reject {rcpt}: {decision.Outcome}");
                continue;
            }

            InboundMessage stored = await this.store.SaveInboundMessageAsync(new InboundMessage
            {
                EnvelopeFrom = ctx.EnvelopeFrom,
                EnvelopeTo = rcpt,
                LocalPart = decision.Address.LocalPart,
                Tag = decision.Address.Tag,
                Subject = parsed.Subject,
                MessageId = parsed.MessageId,
                RawBytes = ctx.RawBytes,
            }, ct);

            var payload = new WebhookPayload
            {
                InboundMessageId = stored.Id,
                Recipient = rcpt,
                LocalPart = decision.Address.LocalPart,
                Tag = decision.Address.Tag,
                CorrelationKey = decision.Grant?.CorrelationKey ?? string.Empty,
                EnvelopeFrom = ctx.EnvelopeFrom,
                Subject = parsed.Subject,
                MessageId = parsed.MessageId,
                ReceivedAt = stored.ReceivedAt,
                RawBytesBase64 = Base64Codec.Encode(ctx.RawBytes),
            };
            WebhookDispatchResult res = await this.dispatcher.SendAsync(decision.Rule.WebhookUrl, decision.Rule.WebhookSecret, payload, ct);
            await this.store.SaveWebhookDeliveryAsync(new WebhookDelivery
            {
                InboundMessageId = stored.Id,
                Url = decision.Rule.WebhookUrl,
                StatusCode = res.StatusCode,
                ErrorMessage = res.ErrorMessage,
            }, ct);

            this.log?.Invoke($"delivered {rcpt} -> HTTP {res.StatusCode}");
            delivered++;
        }

        return delivered == 0
            ? new DeliveryResult { Outcome = DeliveryOutcome.PermanentFailure, ReplyText = "No accepted recipients" }
            : new DeliveryResult { Outcome = DeliveryOutcome.Accepted, ReplyText = $"Accepted for {delivered} recipient(s)" };
    }
}
