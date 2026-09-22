namespace Anjal.Server;

/// <summary>
/// For a polling loop that keeps failing - typically because the database is
/// unreachable. The first failure is logged with its cause; the ones after it
/// are not, and the loop waits longer each time (doubling from the normal
/// interval, up to a ceiling). When a pass succeeds again, that is logged
/// once. A one-hour outage used to write hundreds of identical lines,
/// burying the one that mattered (DEF-026).
/// </summary>
public sealed class FailureBackoff
{
    private readonly string name;
    private readonly System.TimeSpan interval;
    private readonly System.TimeSpan ceiling;
    private readonly System.Action<string>? log;

    /// <summary>Construct.</summary>
    /// <param name="name">What is polling, for the log ("webhook worker").</param>
    /// <param name="interval">The normal wait between passes.</param>
    /// <param name="ceiling">The longest wait while failing.</param>
    /// <param name="log">Optional log.</param>
    public FailureBackoff(string name, System.TimeSpan interval, System.TimeSpan ceiling, System.Action<string>? log)
    {
        System.ArgumentNullException.ThrowIfNull(name);
        this.name = name;
        this.interval = interval;
        this.ceiling = ceiling < interval ? interval : ceiling;
        this.log = log;
    }

    /// <summary>Failures since the last success.</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>A pass succeeded. Returns the wait before the next one.</summary>
    public System.TimeSpan Succeeded()
    {
        if (this.ConsecutiveFailures > 0)
        {
            this.log?.Invoke($"{this.name}: working again after {this.ConsecutiveFailures} failed attempt(s).");
            this.ConsecutiveFailures = 0;
        }
        return this.interval;
    }

    /// <summary>A pass failed. Returns the wait before the next one.</summary>
    /// <param name="ex">What went wrong.</param>
    public System.TimeSpan Failed(System.Exception ex)
    {
        System.ArgumentNullException.ThrowIfNull(ex);
        this.ConsecutiveFailures++;
        if (this.ConsecutiveFailures == 1)
        {
            this.log?.Invoke($"{this.name}: {ex.GetType().Name}: {ex.Message} - retrying quietly, with growing gaps, until it recovers.");
        }
        double factor = System.Math.Pow(2, System.Math.Min(this.ConsecutiveFailures - 1, 16));
        System.TimeSpan wait = System.TimeSpan.FromTicks((long)System.Math.Min(this.interval.Ticks * factor, this.ceiling.Ticks));
        return wait;
    }
}
