using System.Collections.Concurrent;

namespace Anjal.Smtp;

/// <summary>
/// Counts failed SMTP AUTH attempts per client address over a sliding
/// window, shared by every session of a listener. Once an address passes
/// the limit, further AUTH attempts are refused with a temporary failure
/// before the password is checked at all - which also stops the ~50 ms of
/// PBKDF2 per guess from becoming a CPU lever.
/// </summary>
public sealed class AuthFailureLimiter
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<long>> failures = new(System.StringComparer.Ordinal);
    private readonly System.Func<System.DateTimeOffset> clock;
    private long lastSweepTicks;

    /// <summary>Construct.</summary>
    /// <param name="maxFailures">Failures allowed per address within <paramref name="window"/>.</param>
    /// <param name="window">The sliding window.</param>
    /// <param name="clock">Clock, for tests.</param>
    public AuthFailureLimiter(int maxFailures, System.TimeSpan window, System.Func<System.DateTimeOffset>? clock = null)
    {
        System.ArgumentOutOfRangeException.ThrowIfLessThan(maxFailures, 1);
        this.MaxFailures = maxFailures;
        this.Window = window;
        this.clock = clock ?? (() => System.DateTimeOffset.UtcNow);
    }

    /// <summary>Failures allowed per address within <see cref="Window"/>.</summary>
    public int MaxFailures { get; }

    /// <summary>The sliding window.</summary>
    public System.TimeSpan Window { get; }

    /// <summary>Whether this address may attempt AUTH now.</summary>
    /// <param name="remoteAddress">Client address.</param>
    public bool IsAllowed(string remoteAddress)
    {
        System.ArgumentNullException.ThrowIfNull(remoteAddress);
        if (!this.failures.TryGetValue(remoteAddress, out ConcurrentQueue<long>? queue))
        {
            return true;
        }
        long cutoff = (this.clock() - this.Window).UtcTicks;
        while (queue.TryPeek(out long oldest) && oldest < cutoff)
        {
            queue.TryDequeue(out _);
        }
        return queue.Count < this.MaxFailures;
    }

    /// <summary>Record one failed attempt.</summary>
    /// <param name="remoteAddress">Client address.</param>
    public void RecordFailure(string remoteAddress)
    {
        System.ArgumentNullException.ThrowIfNull(remoteAddress);
        long now = this.clock().UtcTicks;
        this.failures.GetOrAdd(remoteAddress, _ => new ConcurrentQueue<long>()).Enqueue(now);
        this.SweepIfDue(now);
    }

    /// <summary>Addresses currently tracked (for tests and metrics).</summary>
    public int TrackedAddresses => this.failures.Count;

    private void SweepIfDue(long nowTicks)
    {
        long last = System.Threading.Interlocked.Read(ref this.lastSweepTicks);
        if (nowTicks - last < System.TimeSpan.FromMinutes(5).Ticks ||
            System.Threading.Interlocked.CompareExchange(ref this.lastSweepTicks, nowTicks, last) != last)
        {
            return;
        }
        long cutoff = nowTicks - this.Window.Ticks;
        foreach (System.Collections.Generic.KeyValuePair<string, ConcurrentQueue<long>> kv in this.failures)
        {
            while (kv.Value.TryPeek(out long oldest) && oldest < cutoff)
            {
                kv.Value.TryDequeue(out _);
            }
            if (kv.Value.IsEmpty)
            {
                this.failures.TryRemove(kv.Key, out _);
            }
        }
    }
}
