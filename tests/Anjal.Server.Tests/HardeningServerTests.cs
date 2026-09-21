using System.Text;
using Anjal.Store;

namespace Anjal.Server.Tests;

public class HardeningServerTests
{
    private sealed class ScriptedDispatcher : Anjal.Routing.IWebhookDispatcher
    {
        private readonly System.Collections.Generic.Queue<int> codes;

        public ScriptedDispatcher(params int[] codes) => this.codes = new(codes);

        public System.Collections.Generic.List<Anjal.Routing.WebhookPayload> Sent { get; } = new();

        public System.Threading.Tasks.Task<Anjal.Routing.WebhookDispatchResult> SendAsync(string url, string secret, Anjal.Routing.WebhookPayload payload, System.Threading.CancellationToken ct = default)
        {
            this.Sent.Add(payload);
            int code = this.codes.Count > 0 ? this.codes.Dequeue() : 200;
            return System.Threading.Tasks.Task.FromResult(new Anjal.Routing.WebhookDispatchResult { StatusCode = code, Completed = code > 0 });
        }
    }

    private static async System.Threading.Tasks.Task<(InMemoryMessageStore Store, WebhookJob Job)> QueuedAsync(System.DateTimeOffset now, System.DateTimeOffset giveUp)
    {
        var store = new InMemoryMessageStore();
        await store.UpsertRoutingRuleAsync(new RoutingRule { LocalPart = "lab", WebhookUrl = "https://hooks.example.com/x", WebhookSecret = "00ff" });
        InboundMessage msg = await store.SaveInboundMessageAsync(new InboundMessage
        {
            EnvelopeFrom = "s@x.test",
            EnvelopeTo = "lab@anjal.test",
            LocalPart = "lab",
            Subject = "Report",
            RawBytes = Encoding.ASCII.GetBytes("Subject: Report\r\n\r\nbody\r\n"),
        });
        WebhookJob job = await store.EnqueueWebhookJobAsync(new WebhookJob
        {
            InboundMessageId = msg.Id,
            Recipient = "lab@anjal.test",
            LocalPart = "lab",
            NextAttemptAt = now,
            GiveUpAt = giveUp,
        });
        return (store, job);
    }

    [Fact]
    public async System.Threading.Tasks.Task Webhook_FailingReceiver_IsRetriedOnSchedule_ThenDelivered()
    {
        var now = new System.DateTimeOffset(2026, 9, 21, 10, 0, 0, System.TimeSpan.Zero);
        (InMemoryMessageStore store, _) = await QueuedAsync(now, now.AddDays(1));
        var dispatcher = new ScriptedDispatcher(503, 0, 200);
        using var worker = new WebhookWorker(store, dispatcher, clock: () => now);

        Assert.Equal(1, await worker.RunOnceAsync());                 // 503
        Assert.Equal(0, await worker.RunOnceAsync());                 // not yet due
        now = now.AddSeconds(1);
        Assert.Equal(1, await worker.RunOnceAsync());                 // connection failure
        now = now.AddSeconds(5);
        Assert.Equal(1, await worker.RunOnceAsync());                 // 200
        Assert.Equal(1, await store.CountWebhookJobsAsync(WebhookJobStatus.Delivered));
        Assert.Equal(3, dispatcher.Sent.Count);
        Assert.Equal("Report", dispatcher.Sent[0].Subject);
        Assert.NotEmpty(dispatcher.Sent[0].RawBytesBase64);
    }

    [Fact]
    public async System.Threading.Tasks.Task Webhook_PastItsGiveUpTime_IsMarkedFailed()
    {
        var now = new System.DateTimeOffset(2026, 9, 21, 10, 0, 0, System.TimeSpan.Zero);
        (InMemoryMessageStore store, _) = await QueuedAsync(now, now.AddMilliseconds(500));
        using var worker = new WebhookWorker(store, new ScriptedDispatcher(500), clock: () => now);
        await worker.RunOnceAsync();
        Assert.Equal(1, await store.CountWebhookJobsAsync(WebhookJobStatus.Failed));
    }

    [Fact]
    public async System.Threading.Tasks.Task Webhook_WhoseRuleWasRemoved_IsAbandoned_NotRetriedForever()
    {
        var now = new System.DateTimeOffset(2026, 9, 21, 10, 0, 0, System.TimeSpan.Zero);
        (InMemoryMessageStore store, _) = await QueuedAsync(now, now.AddDays(1));
        Assert.True(await store.DeleteRoutingRuleAsync("lab"));
        using var worker = new WebhookWorker(store, new ScriptedDispatcher(), clock: () => now);
        await worker.RunOnceAsync();
        Assert.Equal(1, await store.CountWebhookJobsAsync(WebhookJobStatus.Failed));
    }

    [Fact]
    public void BounceNotice_IsAWellFormedDeliveryStatusReport()
    {
        var msg = new OutboundMessage
        {
            EnvelopeFrom = "arun@anjal.co.in",
            EnvelopeTo = "nobody@nowhere.test",
            CreatedAt = new System.DateTimeOffset(2026, 9, 21, 9, 0, 0, System.TimeSpan.Zero),
            RawBytes = Encoding.ASCII.GetBytes("From: arun@anjal.co.in\r\nSubject: Plan\r\n\r\nsecret body text\r\n"),
        };
        string notice = Encoding.UTF8.GetString(BounceNotice.Build("mail.anjal.co.in", msg, "550 5.1.1 User unknown\r\nX-Injected: yes", permanent: true, System.DateTimeOffset.UtcNow));

        Assert.Contains("Content-Type: multipart/report; report-type=delivery-status", notice, System.StringComparison.Ordinal);
        Assert.Contains("Status: 5.0.0", notice, System.StringComparison.Ordinal);
        Assert.Contains("Final-Recipient: rfc822; nobody@nowhere.test", notice, System.StringComparison.Ordinal);
        Assert.Contains("Subject: Plan", notice, System.StringComparison.Ordinal);            // original headers
        Assert.DoesNotContain("secret body text", notice, System.StringComparison.Ordinal);   // not its body
        Assert.Contains("Auto-Submitted: auto-replied", notice, System.StringComparison.Ordinal);
        Assert.DoesNotContain("\r\nX-Injected", notice, System.StringComparison.Ordinal);    // diagnostic kept on one line
        Anjal.Mime.MimeMessage parsed = Anjal.Mime.MimeParser.Parse(Encoding.UTF8.GetBytes(notice));
        Assert.IsType<Anjal.Mime.MimeMultipart>(parsed.Body);
    }

    [Fact]
    public void BounceNotice_ForAGiveUp_SaysSo()
    {
        var msg = new OutboundMessage { EnvelopeFrom = "a@b.test", EnvelopeTo = "c@d.test", RawBytes = new byte[] { 65 } };
        string notice = Encoding.UTF8.GetString(BounceNotice.Build("h.test", msg, "timeout", permanent: false, System.DateTimeOffset.UtcNow));
        Assert.Contains("Status: 4.4.7", notice, System.StringComparison.Ordinal);
        Assert.Contains("retry period ran out", notice, System.StringComparison.Ordinal);
    }
}
