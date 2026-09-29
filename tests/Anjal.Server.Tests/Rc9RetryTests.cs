using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Server.Tests;

/// <summary>
/// v1.0.0-rc.9 (DEF-068, D-53; RFC 5321 4.5.4.1): outgoing mail to a server
/// that stays down is retried for 5 days, not abandoned after 24 hours.
/// </summary>
public sealed class Rc9RetryTests
{
    private sealed class AlwaysDown : IMailSender
    {
        private readonly System.Func<System.DateTimeOffset> clock;

        public AlwaysDown(System.Func<System.DateTimeOffset> clock) => this.clock = clock;

        public List<System.DateTimeOffset> Attempts { get; } = new();

        public Task<SendResult> SendAsync(OutboundDelivery delivery, CancellationToken ct = default)
        {
            this.Attempts.Add(this.clock());
            return Task.FromResult(new SendResult { Outcome = SendOutcome.TransientFailure, ReplyCode = 421, Message = "421 4.3.2 Service not available", RemoteHost = "mx.down.test" });
        }
    }

    [Fact]
    public void TheDefaultGiveUp_IsFiveDays()
    {
        Assert.Equal(System.TimeSpan.FromDays(5), OutboundMessage.DefaultGiveUp);
    }

    [Fact]
    public async Task MailToAServerThatStaysDown_IsRetriedForFiveDays_ThenFails()
    {
        var created = new System.DateTimeOffset(2026, 10, 1, 9, 0, 0, System.TimeSpan.Zero);
        System.DateTimeOffset now = created;
        var store = new InMemoryMessageStore();
        var sender = new AlwaysDown(() => now);
        var worker = new OutboundWorker(store, sender, new OutboundWorkerOptions(), () => now, log: null);
        OutboundMessage queued = await store.EnqueueOutboundAsync(new OutboundMessage
        {
            EnvelopeFrom = "arun@anjal.co.in",
            EnvelopeTo = "doctor@down.test",
            RawBytes = System.Text.Encoding.ASCII.GetBytes("Subject: t\r\n\r\nbody\r\n"),
            CreatedAt = created,
            NextAttemptAt = created,
        });
        Assert.Equal(created + System.TimeSpan.FromDays(5), queued.GiveUpAt);

        OutboundMessage m = queued;
        for (int i = 0; i < 200 && m.Status == OutboundStatus.Pending; i++)
        {
            now = m.NextAttemptAt;
            await worker.DrainOnceAsync();
            m = (await store.GetOutboundByIdAsync(queued.Id))!;
        }

        Assert.Equal(OutboundStatus.Failed, m.Status);
        Assert.True(sender.Attempts.Count >= 15, $"only {sender.Attempts.Count} attempts");
        Assert.Contains(sender.Attempts, a => a - created > System.TimeSpan.FromDays(1));                 // not abandoned after 24 hours
        Assert.True(sender.Attempts[^1] - created >= System.TimeSpan.FromDays(4.5), $"last attempt after {sender.Attempts[^1] - created}");
        Assert.All(sender.Attempts, a => Assert.True(a <= queued.GiveUpAt));
    }
}
