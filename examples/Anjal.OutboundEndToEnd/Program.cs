using System.Net;
using Anjal.Examples.OutboundEndToEnd.Worker;
using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Examples.OutboundEndToEnd;

/// <summary>
/// Demonstrates the full outbound pipeline on localhost. No real network or
/// DNS needed:
///
///   1. Start an in-memory store.
///   2. Start an Anjal SMTP server on an ephemeral port to play "Gmail".
///   3. Configure a <see cref="RelayMailSender"/> pointing at the stub.
///   4. Enqueue a message in the outbound table.
///   5. Run one drain iteration of the worker.
///   6. Print the resulting outbound row and what the stub Gmail received.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        // 1. Store.
        var store = new InMemoryMessageStore();

        // 2. Stub Gmail server.
        var stub = new RecordingSink();
        var stubOptions = new SmtpServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            AdvertisedHostName = "stub.gmail.test",
        };
        using var stubCts = new CancellationTokenSource();
        using var stubServer = new SmtpServer(stubOptions, stub);
        Task stubTask = stubServer.StartAsync(stubCts.Token);
        await Task.Delay(100);
        Console.WriteLine($"Stub Gmail server listening on 127.0.0.1:{stubServer.BoundPort}");

        // 3. Relay sender pointing at the stub.
        var relay = new RelayMailSender(new RelayOptions
        {
            Host = "127.0.0.1",
            Port = stubServer.BoundPort,
            ClientHostName = "anjal.test",
            ConnectTimeout = TimeSpan.FromSeconds(5),
        });

        // 4. Enqueue a message.
        byte[] body = System.Text.Encoding.UTF8.GetBytes(
            "From: noreply@anjal.test\r\n" +
            "To: patient@gmail.com\r\n" +
            "Subject: Password reset link\r\n" +
            "Message-ID: <demo-outbound@anjal.test>\r\n" +
            "\r\n" +
            "Your reset code is 482919.\r\n");
        OutboundMessage queued = await store.EnqueueOutboundAsync(new OutboundMessage
        {
            EnvelopeFrom = "noreply@anjal.test",
            EnvelopeTo = "patient@gmail.com",
            RawBytes = body,
        });
        Console.WriteLine($"Enqueued outbound id={queued.Id}, status={queued.Status}");

        // 5. Run one drain iteration.
        void Log(string line) => Console.WriteLine($"[worker] {line}");
        var worker = new DemoOutboundWorker(store, relay, log: Log);
        int processed = await worker.DrainOnceAsync();
        Console.WriteLine();
        Console.WriteLine($"Drain processed {processed} message(s).");

        // 6. Inspect.
        OutboundMessage updated = store.Outbound[0];
        Console.WriteLine();
        Console.WriteLine("=== Outbound row after drain ===");
        Console.WriteLine($"  id:         {updated.Id}");
        Console.WriteLine($"  to:         {updated.EnvelopeTo}");
        Console.WriteLine($"  status:     {updated.Status}");
        Console.WriteLine($"  attempts:   {updated.Attempts}");
        Console.WriteLine($"  last_error: {(string.IsNullOrEmpty(updated.LastError) ? "(none)" : updated.LastError)}");

        Console.WriteLine();
        Console.WriteLine("=== Stub Gmail received ===");
        Console.WriteLine($"  messages: {stub.Received.Count}");
        if (stub.Received.Count > 0)
        {
            DeliveryContext got = stub.Received[0];
            Console.WriteLine($"  envelope_from: {got.EnvelopeFrom}");
            Console.WriteLine($"  envelope_to:   {string.Join(", ", got.EnvelopeTo)}");
            Console.WriteLine($"  raw bytes:     {got.RawBytes.Length}");
            string preview = System.Text.Encoding.UTF8.GetString(got.RawBytes);
            Console.WriteLine($"  preview:");
            foreach (string line in preview.Split("\r\n"))
            {
                Console.WriteLine($"    {line}");
            }
        }

        stubCts.Cancel();
        try { await stubTask; } catch (OperationCanceledException) { }

        Console.WriteLine();
        Console.WriteLine("Outbound end-to-end demo complete.");
        return 0;
    }
}

/// <summary>Receiver-side sink that records what came in.</summary>
internal sealed class RecordingSink : IMessageSink
{
    public List<DeliveryContext> Received { get; } = new();

    public Task<DeliveryResult> DeliverAsync(DeliveryContext ctx, CancellationToken ct = default)
    {
        this.Received.Add(ctx);
        return Task.FromResult(new DeliveryResult
        {
            Outcome = DeliveryOutcome.Accepted,
            ReplyText = "OK",
        });
    }
}
