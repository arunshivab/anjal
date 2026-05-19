using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Examples.OutboundEndToEnd.Worker;

/// <summary>
/// Local copy of the queue-drain logic from <c>Anjal.Server.OutboundWorker</c>.
/// Duplicated here because <c>Anjal.Server</c> pulls in the Postgres store
/// dependency through its <c>Program.cs</c>, and the example wants to run
/// on a clean Anjal.Store + Anjal.Smtp dependency set.
/// </summary>
internal sealed class DemoOutboundWorker
{
    private static readonly TimeSpan[] BackoffSchedule = new[]
    {
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(24),
    };

    private readonly IMessageStore store;
    private readonly IMailSender sender;
    private readonly Func<DateTimeOffset> clock;
    private readonly Action<string>? log;

    public DemoOutboundWorker(IMessageStore store, IMailSender sender, Func<DateTimeOffset>? clock = null, Action<string>? log = null)
    {
        this.store = store;
        this.sender = sender;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.log = log;
    }

    public async Task<int> DrainOnceAsync(CancellationToken ct = default)
    {
        DateTimeOffset now = this.clock();
        IReadOnlyList<OutboundMessage> batch = await this.store.LeaseOutboundBatchAsync(10, now, ct);

        foreach (OutboundMessage m in batch)
        {
            await this.ProcessOneAsync(m, ct);
        }
        return batch.Count;
    }

    private async Task ProcessOneAsync(OutboundMessage m, CancellationToken ct)
    {
        var delivery = new OutboundDelivery
        {
            EnvelopeFrom = m.EnvelopeFrom,
            EnvelopeTo = new[] { m.EnvelopeTo },
            RawBytes = m.RawBytes,
        };

        SendResult result = await this.sender.SendAsync(delivery, ct);
        DateTimeOffset now = this.clock();

        switch (result.Outcome)
        {
            case SendOutcome.Sent:
                await this.store.MarkOutboundResultAsync(m.Id, OutboundStatus.Sent, now, result.Message, ct);
                this.log?.Invoke($"sent {m.Id} -> {m.EnvelopeTo}");
                break;
            case SendOutcome.PermanentFailure:
                await this.store.MarkOutboundResultAsync(m.Id, OutboundStatus.Failed, now, result.Message, ct);
                this.log?.Invoke($"failed {m.Id}: {result.Message}");
                break;
            default:
                int idx = Math.Min(m.Attempts, BackoffSchedule.Length - 1);
                DateTimeOffset next = now + BackoffSchedule[idx];
                if (next > m.GiveUpAt)
                {
                    await this.store.MarkOutboundResultAsync(m.Id, OutboundStatus.Failed, now, $"give-up: {result.Message}", ct);
                }
                else
                {
                    await this.store.MarkOutboundResultAsync(m.Id, OutboundStatus.Pending, next, result.Message, ct);
                }
                break;
        }
    }
}
