namespace Anjal.Server;

/// <summary>
/// rc.15 (items 59 and 61): what the mail server keeps for the dashboards.
/// A refused submission by an application's key is written to the activity
/// log, so its organisation sees it in the Applications box; and the
/// server's refusal counters are added up per day, so the operator sees
/// the attacks refused. Neither ever holds mail or what was typed.
/// </summary>
public static class ServiceRecords
{
    /// <summary>The kind of the service record holding the refusal counts per day.</summary>
    public const string RefusalsKind = "refusals";

    /// <summary>The counters added up per day, with the words the operator's dashboard shows.</summary>
    public static readonly System.Collections.Generic.IReadOnlyList<string> RefusalCounters = new[]
    {
        "anjal_smtp_connections_refused_total",
        "anjal_smtp_auth_failures_total",
        "anjal_smtp_auth_throttled_total",
        "anjal_spam_rejected_total",
        "anjal_greylist_deferred_total",
        "anjal_smtp_unknown_recipient_total",
        "anjal_smtp_bare_dot_rejected_total",
    };

    /// <summary>
    /// The listener for refused submissions: when the user name is a key the
    /// service made (an application's), the refusal goes to the activity log
    /// as <c>smtp.submission.refused</c>. Other names - guesses - are only counted.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="log">Where a failure to record is reported.</param>
    /// <returns>The listener.</returns>
    public static System.Action<Anjal.Smtp.SubmissionRefusal> RefusalRecorder(Anjal.Store.IMessageStore store, System.Action<string> log)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(log);
        return refusal => _ = RecordAsync(store, refusal, log);
    }

    /// <summary>
    /// Every ten minutes, add what the refusal counters gained to today's
    /// totals in the service record (kept for 400 days). A restart starts the
    /// counters at nought again; nothing is counted twice.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="log">Where a failure is reported.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task that ends when cancelled.</returns>
    public static async System.Threading.Tasks.Task RunRefusalCountsAsync(Anjal.Store.IMessageStore store, System.Action<string> log, System.Threading.CancellationToken ct)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(log);
        var last = new System.Collections.Generic.Dictionary<string, long>(System.StringComparer.Ordinal);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(System.TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
            }
            catch (System.OperationCanceledException)
            {
                return;
            }
            try
            {
                var gained = new System.Collections.Generic.Dictionary<string, long>(System.StringComparer.Ordinal);
                foreach (string name in RefusalCounters)
                {
                    long now = Anjal.Smtp.Counters.Get(name);
                    long before = last.TryGetValue(name, out long b) ? b : 0;
                    if (now > before)
                    {
                        gained[name] = now - before;
                    }
                    last[name] = now;
                }
                if (gained.Count > 0)
                {
                    await AddAsync(store, System.DateTimeOffset.UtcNow, gained, ct).ConfigureAwait(false);
                }
            }
#pragma warning disable CA1031 // Counting is best effort; the mail server never stops for it.
            catch (System.Exception ex) when (ex is not System.OperationCanceledException)
            {
                log($"Refusal counts: not recorded this time ({ex.GetType().Name}: {ex.Message}).");
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>Add counts to a day's totals in the refusals record.</summary>
    /// <param name="store">The store.</param>
    /// <param name="at">When (its day in the server's own time zone is used, as the dashboards' days are).</param>
    /// <param name="gained">What to add, by counter.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async System.Threading.Tasks.Task AddAsync(Anjal.Store.IMessageStore store, System.DateTimeOffset at, System.Collections.Generic.IReadOnlyDictionary<string, long> gained, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(gained);
        string? json = await store.GetServiceRecordAsync(RefusalsKind, ct).ConfigureAwait(false);
        var days = (json is null ? null : System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.SortedDictionary<string, System.Collections.Generic.Dictionary<string, long>>>(json))
            ?? new System.Collections.Generic.SortedDictionary<string, System.Collections.Generic.Dictionary<string, long>>(System.StringComparer.Ordinal);
        string day = at.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (!days.TryGetValue(day, out System.Collections.Generic.Dictionary<string, long>? totals))
        {
            totals = new System.Collections.Generic.Dictionary<string, long>(System.StringComparer.Ordinal);
            days[day] = totals;
        }
        foreach (System.Collections.Generic.KeyValuePair<string, long> g in gained)
        {
            totals[g.Key] = (totals.TryGetValue(g.Key, out long t) ? t : 0) + g.Value;
        }
        while (days.Count > 400)
        {
            days.Remove(System.Linq.Enumerable.First(days.Keys));
        }
        await store.SetServiceRecordAsync(RefusalsKind, System.Text.Json.JsonSerializer.Serialize(days), ct).ConfigureAwait(false);
    }

    private static async System.Threading.Tasks.Task RecordAsync(Anjal.Store.IMessageStore store, Anjal.Smtp.SubmissionRefusal refusal, System.Action<string> log)
    {
        try
        {
            if (refusal.Username.Length == 0 || await store.GetSmtpUserAsync(refusal.Username).ConfigureAwait(false) is null)
            {
                return;
            }
            await store.AppendAuditAsync(new Anjal.Store.AuditEvent
            {
                Actor = refusal.Username,
                Action = "smtp.submission.refused",
                Subject = refusal.Username,
                Detail = refusal.Reason.Length > 200 ? refusal.Reason[..200] : refusal.Reason,
                RemoteAddress = refusal.RemoteAddress,
            }).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Recording is best effort; the refusal itself has already been sent.
        catch (System.Exception ex)
        {
            log($"Refused submission for {refusal.Username}: not written to the activity log ({ex.GetType().Name}).");
        }
#pragma warning restore CA1031
    }
}
