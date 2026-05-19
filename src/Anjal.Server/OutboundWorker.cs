namespace Anjal.Server;

/// <summary>
/// Background worker that drains the outbound queue. Leases batches of
/// pending messages, hands each to an <see cref="Anjal.Smtp.IMailSender"/>,
/// then writes the result back to the store with exponential backoff for
/// transient failures.
///
/// Retry schedule: 1m, 5m, 15m, 1h, 6h, 24h. Permanent failures (5xx) and
/// messages past their <c>give_up_at</c> deadline are marked Failed.
/// </summary>
public sealed class OutboundWorker
{
    private static readonly System.TimeSpan[] BackoffSchedule = new[]
    {
        System.TimeSpan.FromMinutes(1),
        System.TimeSpan.FromMinutes(5),
        System.TimeSpan.FromMinutes(15),
        System.TimeSpan.FromHours(1),
        System.TimeSpan.FromHours(6),
        System.TimeSpan.FromHours(24),
    };

    private readonly Anjal.Store.IMessageStore store;
    private readonly Anjal.Smtp.IMailSender sender;
    private readonly OutboundWorkerOptions options;
    private readonly System.Func<System.DateTimeOffset> clock;
    private readonly System.Action<string>? log;

    /// <summary>
    /// Construct an outbound worker.
    /// </summary>
    /// <param name="store">Store holding the queue.</param>
    /// <param name="sender">Sender to actually deliver messages.</param>
    /// <param name="options">Worker configuration.</param>
    /// <param name="clock">Optional clock for tests.</param>
    /// <param name="log">Optional log callback.</param>
    public OutboundWorker(
        Anjal.Store.IMessageStore store,
        Anjal.Smtp.IMailSender sender,
        OutboundWorkerOptions options,
        System.Func<System.DateTimeOffset>? clock = null,
        System.Action<string>? log = null)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(sender);
        System.ArgumentNullException.ThrowIfNull(options);
        this.store = store;
        this.sender = sender;
        this.options = options;
        this.clock = clock ?? (() => System.DateTimeOffset.UtcNow);
        this.log = log;
    }

    /// <summary>
    /// Run the worker loop until <paramref name="ct"/> is signalled. Each
    /// iteration: lease a batch, send each, mark each, sleep <see cref="OutboundWorkerOptions.PollInterval"/>.
    /// </summary>
    /// <param name="ct">Cancellation that stops the loop.</param>
    /// <returns>A task that completes when the loop exits.</returns>
    public async System.Threading.Tasks.Task RunAsync(System.Threading.CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await this.DrainOnceAsync(ct).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Intentional: never let a worker crash; log and continue.
            catch (System.Exception ex)
            {
                this.log?.Invoke($"outbound worker error: {ex.GetType().Name}: {ex.Message}");
            }
#pragma warning restore CA1031

            try
            {
                await System.Threading.Tasks.Task.Delay(this.options.PollInterval, ct).ConfigureAwait(false);
            }
            catch (System.OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Run a single iteration: lease one batch, process every message in it,
    /// then return. Exposed so tests and demos can drive the worker manually
    /// without waiting on the polling interval.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The number of messages processed.</returns>
    public async System.Threading.Tasks.Task<int> DrainOnceAsync(System.Threading.CancellationToken ct = default)
    {
        System.DateTimeOffset now = this.clock();
        System.Collections.Generic.IReadOnlyList<Anjal.Store.OutboundMessage> batch =
            await this.store.LeaseOutboundBatchAsync(this.options.BatchSize, now, ct).ConfigureAwait(false);

        foreach (Anjal.Store.OutboundMessage m in batch)
        {
            await this.ProcessOneAsync(m, ct).ConfigureAwait(false);
        }
        return batch.Count;
    }

    private async System.Threading.Tasks.Task ProcessOneAsync(Anjal.Store.OutboundMessage m, System.Threading.CancellationToken ct)
    {
        var delivery = new Anjal.Smtp.OutboundDelivery
        {
            EnvelopeFrom = m.EnvelopeFrom,
            EnvelopeTo = new[] { m.EnvelopeTo },
            RawBytes = m.RawBytes,
        };

        Anjal.Smtp.SendResult result;
        try
        {
            result = await this.sender.SendAsync(delivery, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031
        catch (System.Exception ex)
        {
            result = new Anjal.Smtp.SendResult
            {
                Outcome = Anjal.Smtp.SendOutcome.TransientFailure,
                Message = $"sender threw {ex.GetType().Name}: {ex.Message}",
            };
        }
#pragma warning restore CA1031

        System.DateTimeOffset now = this.clock();
        switch (result.Outcome)
        {
            case Anjal.Smtp.SendOutcome.Sent:
                await this.store.MarkOutboundResultAsync(m.Id, Anjal.Store.OutboundStatus.Sent, now, result.Message, ct).ConfigureAwait(false);
                this.log?.Invoke($"sent {m.Id} -> {m.EnvelopeTo}");
                break;

            case Anjal.Smtp.SendOutcome.PermanentFailure:
                await this.store.MarkOutboundResultAsync(m.Id, Anjal.Store.OutboundStatus.Failed, now, result.Message, ct).ConfigureAwait(false);
                this.log?.Invoke($"failed (permanent) {m.Id} -> {m.EnvelopeTo}: {result.Message}");
                break;

            case Anjal.Smtp.SendOutcome.TransientFailure:
            default:
                // Calculate next attempt time. If past give-up, mark Failed.
                int attemptIndex = System.Math.Min(m.Attempts, BackoffSchedule.Length - 1);
                System.DateTimeOffset nextAttempt = now + BackoffSchedule[attemptIndex];
                if (nextAttempt > m.GiveUpAt)
                {
                    await this.store.MarkOutboundResultAsync(m.Id, Anjal.Store.OutboundStatus.Failed, now,
                        $"Give-up reached after {m.Attempts + 1} attempts. Last error: {result.Message}", ct).ConfigureAwait(false);
                    this.log?.Invoke($"failed (give-up) {m.Id} -> {m.EnvelopeTo}");
                }
                else
                {
                    await this.store.MarkOutboundResultAsync(m.Id, Anjal.Store.OutboundStatus.Pending, nextAttempt, result.Message, ct).ConfigureAwait(false);
                    this.log?.Invoke($"retry {m.Id} -> {m.EnvelopeTo} in {BackoffSchedule[attemptIndex]} ({result.Message})");
                }
                break;
        }
    }
}

/// <summary>
/// Configuration for an <see cref="OutboundWorker"/>.
/// </summary>
public sealed class OutboundWorkerOptions
{
    /// <summary>How long to sleep between batches when the queue is empty.</summary>
    public System.TimeSpan PollInterval { get; init; } = System.TimeSpan.FromSeconds(5);

    /// <summary>Maximum messages to lease per iteration.</summary>
    public int BatchSize { get; init; } = 10;
}
