using System.Diagnostics;
using Anjal.Viewing;

namespace Anjal.Viewer;

/// <summary>How to start a child: the program and its arguments.</summary>
/// <param name="FileName">The program.</param>
/// <param name="Arguments">Its arguments (ending with "--child").</param>
public sealed record ChildCommand(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>
    /// The command that starts this same program as a child: the program itself when it is published as one, or the
    /// dotnet host and this program's assembly when it runs as "dotnet Anjal.Viewer.dll".
    /// </summary>
    /// <returns>The command.</returns>
    public static ChildCommand ForThisProgram()
    {
        string[] child = { "--child" };
        string process = Environment.ProcessPath ?? "dotnet";
        string name = Path.GetFileNameWithoutExtension(process);
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return new ChildCommand(process, new[] { Path.Combine(AppContext.BaseDirectory, "Anjal.Viewer.dll"), "--child" });
        }
        return new ChildCommand(process, child);
    }
}

/// <summary>
/// rc.29 (DES-22 §4.1): one single-use child process. It is started and warmed before it is needed, given one job,
/// watched - killed when its deadline passes or its memory passes the limit - and never used again.
/// </summary>
internal sealed class ChildProcess : IDisposable
{
    /// <summary>A child's managed heap limit (512 MB, DES-22 §4.1).</summary>
    public const long DefaultHeapLimit = 512L * 1024 * 1024;

    private readonly Process process;

    private ChildProcess(Process process)
    {
        this.process = process;
    }

    /// <summary>The process's id.</summary>
    public int Id => this.process.Id;

    /// <summary>Starts a child and waits until it says it is ready; null when it does not start or say so in time.</summary>
    /// <param name="command">How to start it.</param>
    /// <param name="readyWait">How long it may take to warm.</param>
    /// <param name="log">Where its log lines go (only those with the child's prefix).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The child, or null.</returns>
    public static Task<ChildProcess?> StartAsync(ChildCommand command, TimeSpan readyWait, TextWriter log, CancellationToken ct) =>
        StartAsync(command, readyWait, log, DefaultHeapLimit, ct);

    /// <summary>Starts a child with a heap limit of its own and waits until it says it is ready.</summary>
    /// <param name="command">How to start it.</param>
    /// <param name="readyWait">How long it may take to warm.</param>
    /// <param name="log">Where its log lines go.</param>
    /// <param name="heapLimit">Its managed heap's hard limit (rc.30: 1 GiB for a DICOM grouping).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The child, or null.</returns>
    public static async Task<ChildProcess?> StartAsync(ChildCommand command, TimeSpan readyWait, TextWriter log, long heapLimit, CancellationToken ct)
    {
        var info = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string a in command.Arguments)
        {
            info.ArgumentList.Add(a);
        }
        // DES-22 §4.1: each child's managed heap is held to 512 MB; the supervisor's 768 MB watch is the backstop.
        // rc.30: a DICOM grouping's child is held to 1 GiB, and stopped past 1 GiB.
        info.Environment["DOTNET_GCHeapHardLimit"] = "0x" + heapLimit.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
        Process? p;
        try
        {
            p = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        if (p is null)
        {
            return null;
        }
        var child = new ChildProcess(p);
        p.ErrorDataReceived += (_, e) =>
        {
            // Only the child's own lines (statuses and exception types) reach the log; a library's text never does.
            if (e.Data is string line && line.StartsWith(ChildHost.LogPrefix, StringComparison.Ordinal))
            {
                lock (log)
                {
                    log.WriteLine(line.Length > 200 ? line[..200] : line);
                }
            }
        };
        p.BeginErrorReadLine();
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(readyWait);
        try
        {
            byte[] one = new byte[1];
            int n = await p.StandardOutput.BaseStream.ReadAsync(one, wait.Token).ConfigureAwait(false);
            if (n == 1 && one[0] == ChildHost.Ready)
            {
                return child;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        child.Dispose();
        return null;
    }

    /// <summary>Gives the child its one job and waits for the answer, stopping it at the deadline or the memory limit.</summary>
    /// <param name="json">The request.</param>
    /// <param name="files">The request's files.</param>
    /// <param name="deadline">How long the job may take.</param>
    /// <param name="memoryLimit">The most memory the process may hold.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The answer, its files, and how it ended.</returns>
    public async Task<(ViewAnswer Answer, List<byte[]> Files, ChildEnd End)> RunAsync(string json, IReadOnlyList<byte[]> files, TimeSpan deadline, long memoryLimit, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(deadline);
        bool overMemory = false;
        Task watch = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested && !this.process.HasExited)
                {
                    this.process.Refresh();
                    if (this.process.WorkingSet64 > memoryLimit)
                    {
                        overMemory = true;
                        this.Kill();
                        return;
                    }
                    await Task.Delay(100, stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }, CancellationToken.None);
        try
        {
            Stream input = this.process.StandardInput.BaseStream;
            await ViewFrames.WriteAsync(input, json, files.Select(f => (ReadOnlyMemory<byte>)f).ToList(), stop.Token).ConfigureAwait(false);
            (string Json, List<byte[]> Files)? frame = await ViewFrames.ReadAsync(this.process.StandardOutput.BaseStream, stop.Token).ConfigureAwait(false);
            if (frame is null)
            {
                return (new ViewAnswer { Status = overMemory ? ViewStatus.TooLarge : ViewStatus.Failed, Detail = overMemory ? "memory" : "child ended" }, new List<byte[]>(), overMemory ? ChildEnd.Memory : ChildEnd.Died);
            }
            return (ViewJson.ReadAnswer(frame.Value.Json), frame.Value.Files, ChildEnd.Answered);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (new ViewAnswer { Status = ViewStatus.TooSlow, Detail = "deadline" }, new List<byte[]>(), ChildEnd.Deadline);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return (new ViewAnswer { Status = overMemory ? ViewStatus.TooLarge : ViewStatus.Failed, Detail = overMemory ? "memory" : ex.GetType().Name }, new List<byte[]>(), overMemory ? ChildEnd.Memory : ChildEnd.Died);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            this.Kill();
            await watch.ConfigureAwait(false);
        }
    }

    /// <summary>Stops the child, if it is still there.</summary>
    public void Kill()
    {
        try
        {
            if (!this.process.HasExited)
            {
                this.process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.Kill();
        this.process.Dispose();
    }
}

/// <summary>How a child's job ended.</summary>
internal enum ChildEnd
{
    /// <summary>It answered.</summary>
    Answered,

    /// <summary>It was stopped at its deadline.</summary>
    Deadline,

    /// <summary>It was stopped for its memory.</summary>
    Memory,

    /// <summary>It ended without an answer.</summary>
    Died,
}
