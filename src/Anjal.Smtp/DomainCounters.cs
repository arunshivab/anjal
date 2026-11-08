using System.Collections.Concurrent;

namespace Anjal.Smtp;

/// <summary>
/// rc.27 (MOM-10, DES-16 §15 point 6): counts kept per recipient domain, so the Monday summary can say
/// how much of the spam refused at the door was meant for each organisation. Only a domain and a number:
/// never an address or anything of the mail. At most 5,000 domains are counted, so a flood of made-up
/// domains cannot grow it without end.
/// </summary>
public static class DomainCounters
{
    /// <summary>Mail refused at the door as spam.</summary>
    public const string SpamRefused = "spam-refused";

    private const int MaxDomains = 5000;

    private static readonly ConcurrentDictionary<string, StrongBox> Values = new(System.StringComparer.Ordinal);

    /// <summary>Count one for a domain.</summary>
    /// <param name="kind">What is counted (<see cref="SpamRefused"/>).</param>
    /// <param name="domain">The recipient's domain.</param>
    public static void Increment(string kind, string domain)
    {
        System.ArgumentNullException.ThrowIfNull(kind);
        System.ArgumentNullException.ThrowIfNull(domain);
        string key = kind + "|" + domain.Trim().ToLowerInvariant();
        if (!Values.ContainsKey(key) && Values.Count >= MaxDomains)
        {
            return;
        }
        System.Threading.Interlocked.Increment(ref Values.GetOrAdd(key, _ => new StrongBox()).Value);
    }

    /// <summary>Every count so far, by "kind|domain" (they only go up).</summary>
    /// <returns>A copy.</returns>
    public static System.Collections.Generic.IReadOnlyDictionary<string, long> Snapshot() =>
        Values.ToDictionary(kv => kv.Key, kv => System.Threading.Interlocked.Read(ref kv.Value.Value), System.StringComparer.Ordinal);

    private sealed class StrongBox
    {
        public long Value;
    }
}
