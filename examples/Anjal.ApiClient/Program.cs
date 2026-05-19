using System.Net;
using System.Net.Http.Json;
using Anjal.Api;
using Anjal.Api.Dto;
using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Examples.ApiClient;

/// <summary>
/// End-to-end demo of the HTTP API + outbound pipeline running in-process.
///
///   1. Spin up a stub SMTP server on an ephemeral port to play "Gmail".
///   2. Spin up the Anjal API server.
///   3. Spin up an outbound worker pointed at the stub.
///   4. Use HttpClient to call POST /api/outbound and GET /api/outbound/{id}.
///   5. Confirm the message was sent and the API reports Sent status.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        const int apiPort = 38901;
        const string apiToken = "demo-secret";

        var store = new InMemoryMessageStore();

        // 1. Stub Gmail server.
        var stub = new RecordingSink();
        using var stubServer = new SmtpServer(new SmtpServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            AdvertisedHostName = "stub.gmail.test",
        }, stub);
        using var stubCts = new CancellationTokenSource();
        Task stubTask = stubServer.StartAsync(stubCts.Token);
        await Task.Delay(100);
        Console.WriteLine($"Stub SMTP server listening on 127.0.0.1:{stubServer.BoundPort}");

        // 2. API server.
        using var apiCts = new CancellationTokenSource();
        using var api = new ApiServer(new ApiOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = apiPort,
            BearerToken = apiToken,
        }, store, line => Console.WriteLine($"[api] {line}"));
        Task apiTask = api.StartAsync(apiCts.Token);
        await Task.Delay(150);
        Console.WriteLine($"API listening on http://127.0.0.1:{apiPort}/");

        // 3. Outbound worker pointed at the stub.
        var sender = new RelayMailSender(new RelayOptions
        {
            Host = "127.0.0.1",
            Port = stubServer.BoundPort,
            ClientHostName = "anjal.test",
            ConnectTimeout = TimeSpan.FromSeconds(5),
        });
        using var workerCts = new CancellationTokenSource();
        var worker = new DemoWorker(store, sender, line => Console.WriteLine($"[worker] {line}"));
        Task workerTask = worker.RunAsync(workerCts.Token);

        // 4. Client calls.
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{apiPort}/") };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiToken);

        Console.WriteLine();
        Console.WriteLine("Posting an outbound message via the API...");
        HttpResponseMessage post = await http.PostAsJsonAsync("api/outbound", new OutboundRequest
        {
            EnvelopeFrom = "noreply@anjal.test",
            EnvelopeTo = "patient@gmail.com",
            Subject = "Password reset",
            BodyText = "Your reset code is 482919.",
        }, ApiJson.Options);
        Console.WriteLine($"  -> {(int)post.StatusCode} {post.StatusCode}");

        OutboundResponse? created = await post.Content.ReadFromJsonAsync<OutboundResponse>(ApiJson.Options);
        if (created is null)
        {
            Console.WriteLine("ERROR: no response body");
            return 1;
        }
        Console.WriteLine($"  -> id={created.Id}, status={created.Status}");

        // 5. Poll for delivery.
        Console.WriteLine();
        Console.WriteLine("Polling status until Sent or 5 seconds...");
        OutboundResponse? latest = created;
        for (int i = 0; i < 25; i++)
        {
            await Task.Delay(200);
            HttpResponseMessage get = await http.GetAsync($"api/outbound/{created.Id}");
            latest = await get.Content.ReadFromJsonAsync<OutboundResponse>(ApiJson.Options);
            if (latest is null)
            {
                continue;
            }
            Console.WriteLine($"  status={latest.Status}, attempts={latest.Attempts}");
            if (latest.Status is "Sent" or "Failed")
            {
                break;
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== Final state ===");
        Console.WriteLine($"  API reports: status={latest?.Status}, attempts={latest?.Attempts}");
        Console.WriteLine($"  Stub received: {stub.Received.Count} message(s)");
        if (stub.Received.Count > 0)
        {
            DeliveryContext got = stub.Received[0];
            Console.WriteLine($"    envelope_from: {got.EnvelopeFrom}");
            Console.WriteLine($"    envelope_to:   {string.Join(", ", got.EnvelopeTo)}");
            Console.WriteLine($"    bytes:         {got.RawBytes.Length}");
        }

        // Cleanup.
        workerCts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }
        apiCts.Cancel();
        try { await apiTask; } catch (OperationCanceledException) { }
        stubCts.Cancel();
        try { await stubTask; } catch (OperationCanceledException) { }

        Console.WriteLine();
        Console.WriteLine("Demo complete.");
        return latest?.Status == "Sent" ? 0 : 1;
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

/// <summary>Polling worker: drains the outbound queue every 200ms.</summary>
internal sealed class DemoWorker
{
    private readonly IMessageStore store;
    private readonly IMailSender sender;
    private readonly Action<string> log;

    public DemoWorker(IMessageStore store, IMailSender sender, Action<string> log)
    {
        this.store = store;
        this.sender = sender;
        this.log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            IReadOnlyList<OutboundMessage> batch = await this.store.LeaseOutboundBatchAsync(10, now, ct);
            foreach (OutboundMessage m in batch)
            {
                SendResult res = await this.sender.SendAsync(new OutboundDelivery
                {
                    EnvelopeFrom = m.EnvelopeFrom,
                    EnvelopeTo = new[] { m.EnvelopeTo },
                    RawBytes = m.RawBytes,
                }, ct);

                OutboundStatus newStatus = res.Outcome switch
                {
                    SendOutcome.Sent => OutboundStatus.Sent,
                    SendOutcome.PermanentFailure => OutboundStatus.Failed,
                    _ => OutboundStatus.Pending,
                };
                DateTimeOffset next = res.Outcome == SendOutcome.Sent
                    ? now
                    : now.AddMinutes(1);
                await this.store.MarkOutboundResultAsync(m.Id, newStatus, next, res.Message, ct);
                this.log($"{newStatus} {m.Id}");
            }
            try { await Task.Delay(200, ct); } catch (OperationCanceledException) { return; }
        }
    }
}
