using System.Collections.Concurrent;

namespace Anjal.Smtp;

/// <summary>
/// Process-wide counters and gauges for operational metrics, rendered by
/// the API's <c>/metrics</c> endpoint in Prometheus text format. Lives in
/// Anjal.Smtp because every other module already references it.
/// Counters only go up; gauges are callbacks evaluated at scrape time.
/// Names follow Prometheus conventions: <c>anjal_&lt;subsystem&gt;_&lt;thing&gt;_total</c>.
/// </summary>
public static class Counters
{
    private static readonly ConcurrentDictionary<string, StrongBox> Values = new(System.StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, (string Help, System.Func<double> Read)> Gauges = new(System.StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> Help = new(System.StringComparer.Ordinal);

    /// <summary>Increment a counter by one (creating it at zero if new).</summary>
    /// <param name="name">Metric name.</param>
    public static void Increment(string name) => Add(name, 1);

    /// <summary>Add to a counter.</summary>
    /// <param name="name">Metric name.</param>
    /// <param name="delta">Amount (non-negative).</param>
    public static void Add(string name, long delta)
    {
        System.ArgumentNullException.ThrowIfNull(name);
        if (delta < 0)
        {
            throw new System.ArgumentOutOfRangeException(nameof(delta), "Counters only increase.");
        }
        StrongBox box = Values.GetOrAdd(name, _ => new StrongBox());
        System.Threading.Interlocked.Add(ref box.Value, delta);
    }

    /// <summary>Read a counter (zero if unknown).</summary>
    /// <param name="name">Metric name.</param>
    public static long Get(string name)
    {
        System.ArgumentNullException.ThrowIfNull(name);
        return Values.TryGetValue(name, out StrongBox? box) ? System.Threading.Interlocked.Read(ref box.Value) : 0;
    }

    /// <summary>Attach help text to a metric (shown as <c># HELP</c>).</summary>
    /// <param name="name">Metric name.</param>
    /// <param name="help">Help text.</param>
    public static void Describe(string name, string help)
    {
        System.ArgumentNullException.ThrowIfNull(name);
        System.ArgumentNullException.ThrowIfNull(help);
        Help[name] = help;
    }

    /// <summary>Register (or replace) a gauge evaluated at scrape time.</summary>
    /// <param name="name">Metric name.</param>
    /// <param name="help">Help text.</param>
    /// <param name="read">Callback returning the current value.</param>
    public static void RegisterGauge(string name, string help, System.Func<double> read)
    {
        System.ArgumentNullException.ThrowIfNull(name);
        System.ArgumentNullException.ThrowIfNull(help);
        System.ArgumentNullException.ThrowIfNull(read);
        Gauges[name] = (help, read);
    }

    /// <summary>Remove a gauge (tests, or a component shutting down).</summary>
    /// <param name="name">Metric name.</param>
    public static void UnregisterGauge(string name)
    {
        System.ArgumentNullException.ThrowIfNull(name);
        Gauges.TryRemove(name, out _);
    }

    /// <summary>
    /// Render everything in Prometheus text exposition format (version
    /// 0.0.4), sorted by name so output is stable.
    /// </summary>
    public static string RenderPrometheus()
    {
        var sb = new System.Text.StringBuilder();
        foreach (string name in Values.Keys.OrderBy(k => k, System.StringComparer.Ordinal))
        {
            if (Help.TryGetValue(name, out string? help))
            {
                sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            }
            sb.Append("# TYPE ").Append(name).Append(" counter\n");
            sb.Append(name).Append(' ').Append(Get(name).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        }
        foreach (string name in Gauges.Keys.OrderBy(k => k, System.StringComparer.Ordinal))
        {
            (string help, System.Func<double> read) = Gauges[name];
            double value;
            try
            {
                value = read();
            }
#pragma warning disable CA1031 // A failing gauge must not break the scrape.
            catch (System.Exception)
            {
                value = double.NaN;
            }
#pragma warning restore CA1031
            sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(" gauge\n");
            sb.Append(name).Append(' ').Append(value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        }
        return sb.ToString();
    }

    private sealed class StrongBox
    {
        public long Value;
    }
}
