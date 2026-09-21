using System.Collections.Concurrent;
using Anjal.Smtp;

namespace Anjal.Spam;

/// <summary>Limits for <see cref="RateLimiter"/>. Zero disables a limit.</summary>
public sealed class RateLimitOptions
{
    /// <summary>Connections per client IP per minute on this listener. Default 60.</summary>
    public int ConnectionsPerMinute { get; init; } = 60;

    /// <summary>Messages (MAIL FROM) per client IP per hour for unauthenticated sessions. Default 200.</summary>
    public int MessagesPerHourPerIp { get; init; } = 200;

    /// <summary>Messages (MAIL FROM) per authenticated user per hour. Default 100.</summary>
    public int MessagesPerHourPerUser { get; init; } = 100;

    /// <summary>
    /// Most keys any one table remembers. Past it, expired keys are swept at
    /// once instead of waiting for the ten-minute sweep, and if that is not
    /// enough the least recently active keys are forgotten. A flood of
    /// distinct source addresses can then cost bounded memory, not the
    /// process. Default 100,000.
    /// </summary>
    public int MaxEntries { get; init; } = 100_000;
}

/// <summary>
/// In-memory sliding-window rate limiter. Counts are per process and
/// reset on restart; that is deliberate for v1 - the goal is to blunt
/// bursts from one source, not to keep a ledger. Windows are pruned
/// lazily on access and idle keys are swept periodically.
/// </summary>
public sealed class RateLimiter : ISmtpPolicy
{
    private readonly RateLimitOptions options;
    private readonly Func<DateTimeOffset> clock;
    private readonly ConcurrentDictionary<string, Window> connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Window> ipMessages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Window> userMessages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keys currently held across the limiter's tables (for metrics and tests).</summary>
    public int TrackedAddresses => this.connections.Count + this.ipMessages.Count + this.userMessages.Count;
    private long lastSweepTicks;

    /// <summary>Construct.</summary>
    /// <param name="options">Limits; null for defaults.</param>
    /// <param name="clock">Time source; null for the system clock. Tests inject a fake.</param>
    public RateLimiter(RateLimitOptions? options = null, Func<DateTimeOffset>? clock = null)
    {
        this.options = options ?? new RateLimitOptions();
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.lastSweepTicks = this.clock().UtcTicks;
    }

    /// <inheritdoc/>
    public Task<PolicyDecision> OnConnectAsync(string remoteAddress, CancellationToken ct = default) => Task.FromResult(this.OnConnect(remoteAddress));

    /// <inheritdoc/>
    public Task<PolicyDecision> OnMailFromAsync(string remoteAddress, string? authenticatedUser, string envelopeFrom, CancellationToken ct = default) =>
        Task.FromResult(this.OnMailFrom(remoteAddress, authenticatedUser, envelopeFrom));

    /// <inheritdoc/>
    public Task<PolicyDecision> OnRcptToAsync(string remoteAddress, string? authenticatedUser, string envelopeFrom, string recipient, CancellationToken ct = default) =>
        Task.FromResult(PolicyDecision.Allow);

    /// <summary>Synchronous connect check.</summary>
    /// <param name="remoteAddress">Client IP.</param>
    public PolicyDecision OnConnect(string remoteAddress)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);
        this.SweepIfDue();
        if (this.options.ConnectionsPerMinute <= 0)
        {
            return PolicyDecision.Allow;
        }
        return this.Hit(this.connections, remoteAddress, TimeSpan.FromMinutes(1), this.options.ConnectionsPerMinute)
            ? PolicyDecision.Allow
            : Refused("anjal_ratelimit_connections_refused_total", PolicyDecision.Defer("4.7.1 Too many connections, try again later", 421));
    }

    /// <summary>Synchronous MAIL FROM check.</summary>
    /// <param name="remoteAddress">Client IP.</param>
    /// <param name="authenticatedUser">Authenticated user, or null.</param>
    /// <param name="envelopeFrom">MAIL FROM address.</param>
    public PolicyDecision OnMailFrom(string remoteAddress, string? authenticatedUser, string envelopeFrom)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);
        if (authenticatedUser is not null)
        {
            if (this.options.MessagesPerHourPerUser <= 0)
            {
                return PolicyDecision.Allow;
            }
            return this.Hit(this.userMessages, authenticatedUser, TimeSpan.FromHours(1), this.options.MessagesPerHourPerUser)
                ? PolicyDecision.Allow
                : Refused("anjal_ratelimit_messages_refused_total", PolicyDecision.Defer("4.7.1 Sending rate limit reached for this account, try again later"));
        }
        if (this.options.MessagesPerHourPerIp <= 0)
        {
            return PolicyDecision.Allow;
        }
        return this.Hit(this.ipMessages, remoteAddress, TimeSpan.FromHours(1), this.options.MessagesPerHourPerIp)
            ? PolicyDecision.Allow
            : Refused("anjal_ratelimit_messages_refused_total", PolicyDecision.Defer("4.7.1 Too many messages from your address, try again later"));
    }

    private static PolicyDecision Refused(string counter, PolicyDecision decision)
    {
        Counters.Increment(counter);
        return decision;
    }

    /// <summary>Record one event and report whether the key is still within its limit.</summary>
    private bool Hit(ConcurrentDictionary<string, Window> table, string key, TimeSpan span, int limit)
    {
        DateTimeOffset now = this.clock();
        if (table.Count >= this.options.MaxEntries && !table.ContainsKey(key))
        {
            this.Shrink(table, now - span);
        }
        Window w = table.GetOrAdd(key, _ => new Window());
        lock (w)
        {
            w.Prune(now - span);
            if (w.Count >= limit)
            {
                return false;
            }
            w.Add(now);
            return true;
        }
    }

    private void SweepIfDue()
    {
        DateTimeOffset now = this.clock();
        long last = Interlocked.Read(ref this.lastSweepTicks);
        if (now.UtcTicks - last < TimeSpan.FromMinutes(10).Ticks)
        {
            return;
        }
        if (Interlocked.CompareExchange(ref this.lastSweepTicks, now.UtcTicks, last) != last)
        {
            return;
        }
        Sweep(this.connections, now - TimeSpan.FromMinutes(1));
        Sweep(this.ipMessages, now - TimeSpan.FromHours(1));
        Sweep(this.userMessages, now - TimeSpan.FromHours(1));
    }

    /// <summary>Sweep now; if still at capacity, drop the least recently active tenth.</summary>
    private void Shrink(ConcurrentDictionary<string, Window> table, DateTimeOffset cutoff)
    {
        Sweep(table, cutoff);
        if (table.Count < this.options.MaxEntries)
        {
            return;
        }
        var byActivity = new List<(DateTimeOffset Last, string Key)>(table.Count);
        foreach (KeyValuePair<string, Window> kv in table)
        {
            lock (kv.Value)
            {
                byActivity.Add((kv.Value.Last, kv.Key));
            }
        }
        byActivity.Sort((a, b) => a.Last.CompareTo(b.Last));
        int drop = Math.Max(1, table.Count / 10);
        for (int i = 0; i < drop && i < byActivity.Count; i++)
        {
            table.TryRemove(byActivity[i].Key, out _);
        }
    }

    private static void Sweep(ConcurrentDictionary<string, Window> table, DateTimeOffset cutoff)
    {
        foreach (KeyValuePair<string, Window> kv in table)
        {
            lock (kv.Value)
            {
                kv.Value.Prune(cutoff);
                if (kv.Value.Count == 0)
                {
                    table.TryRemove(kv.Key, out _);
                }
            }
        }
    }

    private sealed class Window
    {
        private readonly Queue<DateTimeOffset> hits = new();

        public int Count => this.hits.Count;

        public DateTimeOffset Last { get; private set; } = DateTimeOffset.MinValue;

        public void Add(DateTimeOffset at)
        {
            this.hits.Enqueue(at);
            this.Last = at;
        }

        public void Prune(DateTimeOffset cutoff)
        {
            while (this.hits.Count > 0 && this.hits.Peek() < cutoff)
            {
                this.hits.Dequeue();
            }
        }
    }
}

/// <summary>Settings for <see cref="Greylist"/>.</summary>
public sealed class GreylistOptions
{
    /// <summary>How long a new (IP, sender, recipient) triplet is deferred. Default 5 minutes.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a triplet that never retried is remembered before being forgotten. Default 8 hours.</summary>
    public TimeSpan PendingLifetime { get; init; } = TimeSpan.FromHours(8);

    /// <summary>How long a triplet that passed stays whitelisted after its last use. Default 36 hours.</summary>
    public TimeSpan PassedLifetime { get; init; } = TimeSpan.FromHours(36);

    /// <summary>
    /// Most triplets remembered. Past it the table is swept immediately and,
    /// if still full, the least recently seen triplets are forgotten - which
    /// only means those senders are greylisted once more. Default 200,000.
    /// </summary>
    public int MaxEntries { get; init; } = 200_000;
}

/// <summary>
/// Classic greylisting. The first time a given (client /24 or /64, sender,
/// recipient) triplet is seen, RCPT is deferred with 451. A retry after
/// <see cref="GreylistOptions.Delay"/> passes and the triplet is remembered;
/// legitimate MTAs retry, most spam cannons do not. Authenticated sessions
/// and loopback/private clients are never greylisted. State is in-memory.
/// </summary>
public sealed class Greylist : ISmtpPolicy
{
    private readonly GreylistOptions options;
    private readonly Func<DateTimeOffset> clock;
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private long lastSweepTicks;

    /// <summary>Construct.</summary>
    /// <param name="options">Timings; null for defaults.</param>
    /// <param name="clock">Time source; null for the system clock.</param>
    public Greylist(GreylistOptions? options = null, Func<DateTimeOffset>? clock = null)
    {
        this.options = options ?? new GreylistOptions();
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.lastSweepTicks = this.clock().UtcTicks;
    }

    /// <summary>Number of triplets currently remembered (pending or passed).</summary>
    public int Count => this.entries.Count;

    /// <inheritdoc/>
    public Task<PolicyDecision> OnConnectAsync(string remoteAddress, CancellationToken ct = default) => Task.FromResult(PolicyDecision.Allow);

    /// <inheritdoc/>
    public Task<PolicyDecision> OnMailFromAsync(string remoteAddress, string? authenticatedUser, string envelopeFrom, CancellationToken ct = default) => Task.FromResult(PolicyDecision.Allow);

    /// <inheritdoc/>
    public Task<PolicyDecision> OnRcptToAsync(string remoteAddress, string? authenticatedUser, string envelopeFrom, string recipient, CancellationToken ct = default) =>
        Task.FromResult(this.OnRcptTo(remoteAddress, authenticatedUser, envelopeFrom, recipient));

    /// <summary>Synchronous RCPT TO check.</summary>
    /// <param name="remoteAddress">Client IP.</param>
    /// <param name="authenticatedUser">Authenticated user, or null.</param>
    /// <param name="envelopeFrom">MAIL FROM address.</param>
    /// <param name="recipient">RCPT TO address.</param>
    public PolicyDecision OnRcptTo(string remoteAddress, string? authenticatedUser, string envelopeFrom, string recipient)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);
        ArgumentNullException.ThrowIfNull(envelopeFrom);
        ArgumentNullException.ThrowIfNull(recipient);
        if (authenticatedUser is not null || IsExempt(remoteAddress))
        {
            return PolicyDecision.Allow;
        }
        this.SweepIfDue();

        string key = NetworkKey(remoteAddress) + "|" + envelopeFrom.ToLowerInvariant() + "|" + recipient.ToLowerInvariant();
        DateTimeOffset now = this.clock();
        if (this.entries.Count >= this.options.MaxEntries && !this.entries.ContainsKey(key))
        {
            this.Shrink(now);
        }
        Entry e = this.entries.GetOrAdd(key, _ => new Entry { FirstSeen = now, LastSeen = now, Passed = false });
        lock (e)
        {
            e.LastSeen = now;
            if (e.Passed)
            {
                return PolicyDecision.Allow;
            }
            if (now - e.FirstSeen >= this.options.Delay)
            {
                e.Passed = true;
                return PolicyDecision.Allow;
            }
            int seconds = (int)Math.Ceiling((this.options.Delay - (now - e.FirstSeen)).TotalSeconds);
            Counters.Increment("anjal_greylist_deferred_total");
            return PolicyDecision.Defer($"4.7.1 Greylisted, please retry in {seconds} seconds");
        }
    }

    /// <summary>
    /// Group clients by network so a sending pool with several outbound
    /// IPs (common for large providers) counts as one: /24 for IPv4,
    /// /64 for IPv6.
    /// </summary>
    /// <param name="remoteAddress">Client IP.</param>
    public static string NetworkKey(string remoteAddress)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);
        if (!System.Net.IPAddress.TryParse(remoteAddress, out System.Net.IPAddress? ip))
        {
            return remoteAddress;
        }
        byte[] b = ip.GetAddressBytes();
        if (b.Length == 4)
        {
            return $"{b[0]}.{b[1]}.{b[2]}.0/24";
        }
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 8; i += 2)
        {
            if (i > 0)
            {
                sb.Append(':');
            }
            sb.Append(((b[i] << 8) | b[i + 1]).ToString("x", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.Append("::/64").ToString();
    }

    private static bool IsExempt(string remoteAddress)
    {
        if (!System.Net.IPAddress.TryParse(remoteAddress, out System.Net.IPAddress? ip))
        {
            return false;
        }
        if (System.Net.IPAddress.IsLoopback(ip))
        {
            return true;
        }
        byte[] b = ip.GetAddressBytes();
        if (b.Length == 4)
        {
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
    }

    /// <summary>Forget expired triplets now; if still full, the least recently seen tenth.</summary>
    private void Shrink(DateTimeOffset now)
    {
        var live = new List<(DateTimeOffset Last, string Key)>(this.entries.Count);
        foreach (KeyValuePair<string, Entry> kv in this.entries)
        {
            TimeSpan idle = now - kv.Value.LastSeen;
            bool expired = kv.Value.Passed ? idle > this.options.PassedLifetime : idle > this.options.PendingLifetime;
            if (expired)
            {
                this.entries.TryRemove(kv.Key, out _);
            }
            else
            {
                live.Add((kv.Value.LastSeen, kv.Key));
            }
        }
        if (this.entries.Count < this.options.MaxEntries)
        {
            return;
        }
        live.Sort((a, b) => a.Last.CompareTo(b.Last));
        int drop = Math.Max(1, live.Count / 10);
        for (int k = 0; k < drop && k < live.Count; k++)
        {
            this.entries.TryRemove(live[k].Key, out _);
        }
    }

    private void SweepIfDue()
    {
        DateTimeOffset now = this.clock();
        long last = Interlocked.Read(ref this.lastSweepTicks);
        if (now.UtcTicks - last < TimeSpan.FromMinutes(10).Ticks)
        {
            return;
        }
        if (Interlocked.CompareExchange(ref this.lastSweepTicks, now.UtcTicks, last) != last)
        {
            return;
        }
        foreach (KeyValuePair<string, Entry> kv in this.entries)
        {
            bool expired;
            lock (kv.Value)
            {
                TimeSpan idle = now - kv.Value.LastSeen;
                expired = kv.Value.Passed ? idle > this.options.PassedLifetime : idle > this.options.PendingLifetime;
            }
            if (expired)
            {
                this.entries.TryRemove(kv.Key, out _);
            }
        }
    }

    private sealed class Entry
    {
        public DateTimeOffset FirstSeen { get; set; }

        public DateTimeOffset LastSeen { get; set; }

        public bool Passed { get; set; }
    }
}

/// <summary>
/// Chains several policies; the first refusal wins.
/// </summary>
public sealed class CompositeSmtpPolicy : ISmtpPolicy
{
    private readonly IReadOnlyList<ISmtpPolicy> policies;

    /// <summary>Construct.</summary>
    /// <param name="policies">Policies in evaluation order.</param>
    public CompositeSmtpPolicy(params ISmtpPolicy[] policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        this.policies = policies;
    }

    /// <inheritdoc/>
    public async Task<PolicyDecision> OnConnectAsync(string remoteAddress, CancellationToken ct = default)
    {
        foreach (ISmtpPolicy p in this.policies)
        {
            PolicyDecision d = await p.OnConnectAsync(remoteAddress, ct).ConfigureAwait(false);
            if (!d.Allowed)
            {
                return d;
            }
        }
        return PolicyDecision.Allow;
    }

    /// <inheritdoc/>
    public async Task<PolicyDecision> OnMailFromAsync(string remoteAddress, string? authenticatedUser, string envelopeFrom, CancellationToken ct = default)
    {
        foreach (ISmtpPolicy p in this.policies)
        {
            PolicyDecision d = await p.OnMailFromAsync(remoteAddress, authenticatedUser, envelopeFrom, ct).ConfigureAwait(false);
            if (!d.Allowed)
            {
                return d;
            }
        }
        return PolicyDecision.Allow;
    }

    /// <inheritdoc/>
    public async Task<PolicyDecision> OnRcptToAsync(string remoteAddress, string? authenticatedUser, string envelopeFrom, string recipient, CancellationToken ct = default)
    {
        foreach (ISmtpPolicy p in this.policies)
        {
            PolicyDecision d = await p.OnRcptToAsync(remoteAddress, authenticatedUser, envelopeFrom, recipient, ct).ConfigureAwait(false);
            if (!d.Allowed)
            {
                return d;
            }
        }
        return PolicyDecision.Allow;
    }
}
