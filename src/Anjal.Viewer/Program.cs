using System.Globalization;

namespace Anjal.Viewer;

/// <summary>
/// rc.29 (DES-22 §4, D-358): anjal-viewer. Run with no arguments (or <c>--socket</c>) it is the supervisor, as the
/// <c>anjal-viewer</c> service runs it; with <c>--child</c> it is one single-use child, as the supervisor starts it.
/// </summary>
/// <remarks>
/// Settings: <c>--socket PATH</c> (or <c>ANJAL_VIEWER_SOCKET</c>; default <c>/run/anjal-viewer/viewer.sock</c>),
/// <c>--ready N</c> warmed children kept ready (2), <c>--running N</c> jobs at once (2), <c>--parent PID</c> to stop
/// when that process ends (the webmail that started it, on a developer's machine).
/// </remarks>
public static class Program
{
    /// <summary>The entry point.</summary>
    /// <param name="args">The arguments.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Contains("--child", StringComparer.Ordinal))
        {
            await using Stream input = Console.OpenStandardInput();
            await using Stream output = Console.OpenStandardOutput();
            return await ChildHost.RunAsync(input, output).ConfigureAwait(false);
        }
        var options = new SupervisorOptions
        {
            SocketPath = Value(args, "--socket") ?? Environment.GetEnvironmentVariable("ANJAL_VIEWER_SOCKET") ?? "/run/anjal-viewer/viewer.sock",
            ReadyChildren = Number(args, "--ready") ?? 2,
            Running = Number(args, "--running") ?? 2,
            ParentProcessId = Number(args, "--parent"),
        };
        await using var supervisor = new ViewerSupervisor(options);
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();
        supervisor.Start();
        Console.WriteLine(ChildHost.LogPrefix + "listening on " + options.SocketPath + "; " + options.ReadyChildren + " children kept ready, " + options.Running + " at once");
        await supervisor.RunAsync(stop.Token).ConfigureAwait(false);
        Console.WriteLine(ChildHost.LogPrefix + "stopped after " + supervisor.Jobs + " jobs (" + supervisor.KilledByDeadline + " stopped at their deadline, " + supervisor.KilledByMemory + " for their memory)");
        return 0;
    }

    private static string? Value(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int? Number(string[] args, string name) =>
        Value(args, name) is string v && int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;
}
