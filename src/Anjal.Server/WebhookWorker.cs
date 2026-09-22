namespace Anjal.Server;

/// <summary>
/// Delivers queued webhook notifications. Each job is leased, its payload
/// rebuilt from the stored inbound message, signed with the routing rule's
/// current secret, and sent. A 2xx answer completes it; anything else is
/// retried on the schedule 1 s, 5 s, 25 s, 125 s, then every 10 minutes
/// until the job's give-up time. Every attempt is logged in the existing
/// webhook attempt table.
/// <para>
/// The queue lives in the database, so a restart loses nothing; the worker
/// is also woken immediately when a job is queued, so a healthy receiver
/// hears about a message within milliseconds rather than at the next poll.
/// </para>
/// </summary>
public sealed class WebhookWorker : System.IDisposable
{
    /// <summary>Delay before each retry, by attempt number; the last entry repeats.</summary>
    public static readonly System.TimeSpan[] RetrySchedule =
    {
        System.TimeSpan.FromSeconds(1),
        System.TimeSpan.FromSeconds(5),
        System.TimeSpan.FromSeconds(25),
        System.TimeSpan.FromSeconds(125),
        System.TimeSpan.FromMinutes(10),
    };

    /// <summary>How long a notification is retried before it is marked failed.</summary>
    public static readonly System.TimeSpan GiveUpAfter = System.TimeSpan.FromHours(24);

    private readonly Anjal.Store.IMessageStore store;
    private readonly Anjal.Routing.IWebhookDispatcher dispatcher;
    private readonly System.Action<string>? log;
    private readonly System.Func<System.DateTimeOffset> clock;
    private readonly System.Threading.SemaphoreSlim wake = new(0, 1);
    private readonly System.TimeSpan pollInterval;

    /// <summary>Construct.</summary>
    /// <param name="store">The queue and the inbound messages.</param>
    /// <param name="dispatcher">Sends one signed notification.</param>
    /// <param name="log">Optional log.</param>
    /// <param name="clock">Clock, for tests.</param>
    /// <param name="pollInterval">How often to look for due retries when not woken. Default 2 seconds.</param>
    public WebhookWorker(
        Anjal.Store.IMessageStore store,
        Anjal.Routing.IWebhookDispatcher dispatcher,
        System.Action<string>? log = null,
        System.Func<System.DateTimeOffset>? clock = null,
        System.TimeSpan? pollInterval = null)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(dispatcher);
        this.store = store;
        this.dispatcher = dispatcher;
        this.log = log;
        this.clock = clock ?? (() => System.DateTimeOffset.UtcNow);
        this.pollInterval = pollInterval ?? System.TimeSpan.FromSeconds(2);
    }

    /// <inheritdoc/>
    public void Dispose() => this.wake.Dispose();

    /// <summary>Signal that a job was queued, so it is picked up now.</summary>
    public void Wake()
    {
        if (this.wake.CurrentCount == 0)
        {
            try
            {
                this.wake.Release();
            }
            catch (System.Threading.SemaphoreFullException)
            {
                // Already signalled.
            }
        }
    }

    /// <summary>Run until cancelled.</summary>
    /// <param name="ct">Cancellation.</param>
    public async System.Threading.Tasks.Task RunAsync(System.Threading.CancellationToken ct)
    {
        var backoff = new FailureBackoff("webhook worker", this.pollInterval, System.TimeSpan.FromSeconds(60), this.log);
        while (!ct.IsCancellationRequested)
        {
            System.TimeSpan wait;
            try
            {
                while (await this.RunOnceAsync(ct).ConfigureAwait(false) > 0)
                {
                    // Keep draining while there is work.
                }
                wait = backoff.Succeeded();
            }
            catch (System.OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failed pass is logged once, then retried with backoff.
            catch (System.Exception ex)
            {
                wait = backoff.Failed(ex);
            }
#pragma warning restore CA1031

            try
            {
                await this.wake.WaitAsync(wait, ct).ConfigureAwait(false);
            }
            catch (System.OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Process one batch of due jobs. Returns how many were attempted.</summary>
    /// <param name="ct">Cancellation.</param>
    public async System.Threading.Tasks.Task<int> RunOnceAsync(System.Threading.CancellationToken ct = default)
    {
        System.Collections.Generic.IReadOnlyList<Anjal.Store.WebhookJob> jobs =
            await this.store.LeaseWebhookJobsAsync(10, this.clock(), ct).ConfigureAwait(false);
        foreach (Anjal.Store.WebhookJob job in jobs)
        {
            await this.AttemptAsync(job, ct).ConfigureAwait(false);
        }
        return jobs.Count;
    }

    private async System.Threading.Tasks.Task AttemptAsync(Anjal.Store.WebhookJob job, System.Threading.CancellationToken ct)
    {
        Anjal.Store.RoutingRule? rule = await this.store.GetRoutingRuleAsync(job.LocalPart, ct).ConfigureAwait(false);
        Anjal.Store.InboundMessage? message = await this.store.GetInboundByIdAsync(job.InboundMessageId, ct).ConfigureAwait(false);
        System.DateTimeOffset now = this.clock();
        if (rule is null || message is null)
        {
            string why = rule is null ? $"no routing rule for '{job.LocalPart}' any more" : "inbound message no longer exists";
            await this.store.CompleteWebhookJobAsync(job.Id, Anjal.Store.WebhookJobStatus.Failed, now, why, ct).ConfigureAwait(false);
            this.log?.Invoke($"webhook {job.Id} abandoned: {why}");
            return;
        }

        var payload = new Anjal.Routing.WebhookPayload
        {
            InboundMessageId = message.Id,
            Recipient = job.Recipient,
            LocalPart = job.LocalPart,
            Tag = job.Tag,
            CorrelationKey = job.CorrelationKey,
            EnvelopeFrom = message.EnvelopeFrom,
            Subject = message.Subject,
            MessageId = message.MessageId,
            ReceivedAt = message.ReceivedAt,
            RawBytesBase64 = Anjal.Mime.Base64Codec.Encode(message.RawBytes),
            AuthResultsJson = job.AuthResultsJson,
        };

        Anjal.Routing.WebhookDispatchResult result;
        try
        {
            result = await this.dispatcher.SendAsync(rule.WebhookUrl, rule.WebhookSecret, payload, ct).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A dispatcher failure is a failed attempt, retried on schedule.
        catch (System.Exception ex)
        {
            result = new Anjal.Routing.WebhookDispatchResult { StatusCode = 0, Completed = false, ErrorMessage = ex.GetType().Name + ": " + ex.Message };
        }
#pragma warning restore CA1031

        await this.store.SaveWebhookDeliveryAsync(new Anjal.Store.WebhookDelivery
        {
            InboundMessageId = message.Id,
            Url = rule.WebhookUrl,
            StatusCode = result.StatusCode,
            ErrorMessage = result.ErrorMessage,
        }, ct).ConfigureAwait(false);

        bool ok = result.Completed && result.StatusCode >= 200 && result.StatusCode < 300;
        if (ok)
        {
            await this.store.CompleteWebhookJobAsync(job.Id, Anjal.Store.WebhookJobStatus.Delivered, now, string.Empty, ct).ConfigureAwait(false);
            Anjal.Smtp.Counters.Increment("anjal_webhook_delivered_total");
            this.log?.Invoke($"webhook {job.Id} -> {rule.WebhookUrl} (HTTP {result.StatusCode})");
            return;
        }

        string error = result.ErrorMessage.Length > 0 ? result.ErrorMessage : $"HTTP {result.StatusCode}";
        System.DateTimeOffset next = now + RetrySchedule[System.Math.Min(job.Attempts, RetrySchedule.Length - 1)];
        if (next > job.GiveUpAt)
        {
            await this.store.CompleteWebhookJobAsync(job.Id, Anjal.Store.WebhookJobStatus.Failed, now, "Gave up: " + error, ct).ConfigureAwait(false);
            Anjal.Smtp.Counters.Increment("anjal_webhook_failed_total");
            this.log?.Invoke($"webhook {job.Id} failed for good after {job.Attempts + 1} attempts: {error}");
        }
        else
        {
            await this.store.CompleteWebhookJobAsync(job.Id, Anjal.Store.WebhookJobStatus.Pending, next, error, ct).ConfigureAwait(false);
            Anjal.Smtp.Counters.Increment("anjal_webhook_retries_total");
            this.log?.Invoke($"webhook {job.Id} retry in {(next - now).TotalSeconds:0}s: {error}");
        }
    }
}
