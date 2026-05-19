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
    private readonly Anjal.Dkim.IDkimKeyResolver? dkimResolver;
    private readonly Anjal.Dkim.DkimSigner? dkimSigner;
    private readonly bool requireDkim;

    /// <summary>
    /// Construct an outbound worker.
    /// </summary>
    /// <param name="store">Store holding the queue.</param>
    /// <param name="sender">Sender to actually deliver messages.</param>
    /// <param name="options">Worker configuration.</param>
    /// <param name="clock">Optional clock for tests.</param>
    /// <param name="log">Optional log callback.</param>
    /// <param name="dkimResolver">Optional DKIM key resolver. When supplied,
    /// outbound messages are signed before send. When <paramref name="requireDkim"/>
    /// is true and no key is found for the sender domain, the send hard-fails.</param>
    /// <param name="dkimSigner">Optional pre-configured DKIM signer.</param>
    /// <param name="requireDkim">When true, refuse to send messages whose
    /// sender domain has no configured DKIM key.</param>
    public OutboundWorker(
        Anjal.Store.IMessageStore store,
        Anjal.Smtp.IMailSender sender,
        OutboundWorkerOptions options,
        System.Func<System.DateTimeOffset>? clock = null,
        System.Action<string>? log = null,
        Anjal.Dkim.IDkimKeyResolver? dkimResolver = null,
        Anjal.Dkim.DkimSigner? dkimSigner = null,
        bool requireDkim = false)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(sender);
        System.ArgumentNullException.ThrowIfNull(options);
        this.store = store;
        this.sender = sender;
        this.options = options;
        this.clock = clock ?? (() => System.DateTimeOffset.UtcNow);
        this.log = log;
        this.dkimResolver = dkimResolver;
        this.dkimSigner = dkimSigner;
        this.requireDkim = requireDkim;
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
        byte[] bytesToSend = m.RawBytes;

        // DKIM signing (RFC 6376): sign before handing to the sender so the
        // wire bytes match the signed canonicalization. If signing is required
        // and no key is found, hard-fail this message permanently.
        if (this.dkimResolver is not null && this.dkimSigner is not null)
        {
            string? senderDomain = ExtractFromDomain(bytesToSend);
            if (senderDomain is null)
            {
                if (this.requireDkim)
                {
                    await this.MarkPermanentFailureAsync(m, "DKIM signing required but no From header found", ct).ConfigureAwait(false);
                    return;
                }
                this.log?.Invoke($"DKIM: {m.Id} has no From header; sending unsigned (requireDkim=false)");
            }
            else
            {
                Anjal.Dkim.DkimKey? key = await this.dkimResolver.ResolveAsync(senderDomain, ct).ConfigureAwait(false);
                if (key is null)
                {
                    if (this.requireDkim)
                    {
                        await this.MarkPermanentFailureAsync(m, $"DKIM signing required but no key configured for sender domain '{senderDomain}'", ct).ConfigureAwait(false);
                        return;
                    }
                    this.log?.Invoke($"DKIM: no key for {senderDomain}; sending unsigned (requireDkim=false)");
                }
                else
                {
                    try
                    {
                        bytesToSend = this.dkimSigner.Sign(bytesToSend, key);
                    }
#pragma warning disable CA1031 // Failed signing should not crash the worker.
                    catch (System.Exception ex)
                    {
                        if (this.requireDkim)
                        {
                            await this.MarkPermanentFailureAsync(m, $"DKIM signing failed for {senderDomain}: {ex.GetType().Name}: {ex.Message}", ct).ConfigureAwait(false);
                            return;
                        }
                        this.log?.Invoke($"DKIM signing failed for {senderDomain}, sending unsigned: {ex.GetType().Name}: {ex.Message}");
                    }
#pragma warning restore CA1031
                }
            }
        }
        else if (this.requireDkim)
        {
            // Configuration error: requireDkim is true but no resolver/signer was supplied.
            await this.MarkPermanentFailureAsync(m, "DKIM signing required but no resolver or signer was configured", ct).ConfigureAwait(false);
            return;
        }

        var delivery = new Anjal.Smtp.OutboundDelivery
        {
            EnvelopeFrom = m.EnvelopeFrom,
            EnvelopeTo = new[] { m.EnvelopeTo },
            RawBytes = bytesToSend,
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

    /// <summary>
    /// Extract the domain from the message's <c>From:</c> header. Returns
    /// null if no From header is found or its value has no @-domain.
    /// </summary>
    private static string? ExtractFromDomain(byte[] rawBytes)
    {
        try
        {
            Anjal.Dkim.DkimMessage parsed = Anjal.Dkim.DkimMessage.Parse(rawBytes);
            string? fromValue = parsed.GetHeaderValue("From");
            if (fromValue is null) return null;

            // From may be "Display Name <user@domain>" or just "user@domain".
            int lt = fromValue.IndexOf('<', System.StringComparison.Ordinal);
            int gt = fromValue.IndexOf('>', System.StringComparison.Ordinal);
            string addr = (lt >= 0 && gt > lt) ? fromValue.Substring(lt + 1, gt - lt - 1) : fromValue.Trim();
            int at = addr.LastIndexOf('@');
            if (at < 0 || at == addr.Length - 1) return null;
            return addr.Substring(at + 1).Trim().ToLowerInvariant();
        }
#pragma warning disable CA1031
        catch (System.Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    private async System.Threading.Tasks.Task MarkPermanentFailureAsync(Anjal.Store.OutboundMessage m, string reason, System.Threading.CancellationToken ct)
    {
        System.DateTimeOffset now = this.clock();
        await this.store.MarkOutboundResultAsync(m.Id, Anjal.Store.OutboundStatus.Failed, now, reason, ct).ConfigureAwait(false);
        this.log?.Invoke($"failed (DKIM) {m.Id} -> {m.EnvelopeTo}: {reason}");
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
