using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;
using Anjal.Viewing;

namespace Anjal.Viewer;

/// <summary>rc.29 (DES-22 §4.1, D-358): how the supervisor runs.</summary>
public sealed class SupervisorOptions
{
    /// <summary>The Unix socket the webmail speaks to.</summary>
    public string SocketPath { get; init; } = "/run/anjal-viewer/viewer.sock";

    /// <summary>How many warmed children are kept ready (2, DES-22 §4.1).</summary>
    public int ReadyChildren { get; init; } = 2;

    /// <summary>How many jobs run at once (2: the service's 1.5 GB holds two children at their 768 MB).</summary>
    public int Running { get; init; } = 2;

    /// <summary>The most jobs one person may have at once, running or waiting (4).</summary>
    public int MaxPerPerson { get; init; } = 4;

    /// <summary>The most jobs waiting in all (32); past it a request is answered "busy", never dropped silently.</summary>
    public int MaxWaiting { get; init; } = 32;

    /// <summary>A child holding more memory than this is stopped (768 MB).</summary>
    public long ChildMemoryLimit { get; init; } = 768L * 1024 * 1024;

    /// <summary>How long a child may take to warm before it is given up.</summary>
    public TimeSpan ReadyWait { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How a child is started.</summary>
    public ChildCommand Child { get; init; } = ChildCommand.ForThisProgram();

    /// <summary>When set, the supervisor stops when this process (the webmail that started it) is gone.</summary>
    public int? ParentProcessId { get; init; }

    /// <summary>Where the log goes.</summary>
    public TextWriter Log { get; init; } = Console.Out;
}

/// <summary>
/// rc.29 (DES-22 §4.1, D-358): the viewer's main process. It listens on its socket, keeps warmed single-use child
/// processes ready, shares the children fairly (four jobs a person, 32 waiting in all), gives each job to a fresh
/// child, and stops a child at its job's deadline or when it passes 768 MB. It never reads a file itself.
/// </summary>
public sealed class ViewerSupervisor : IAsyncDisposable
{
    private readonly SupervisorOptions options;
    private readonly Channel<ChildProcess> ready;
    private readonly SemaphoreSlim running;
    private readonly ConcurrentDictionary<string, int> perPerson = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopping = new();
    private readonly object countLock = new();
    private Socket? listener;
    private int waiting;
    private Task? filler;

    /// <summary>Creates the supervisor.</summary>
    /// <param name="options">How it runs.</param>
    public ViewerSupervisor(SupervisorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        this.ready = Channel.CreateBounded<ChildProcess>(new BoundedChannelOptions(Math.Max(1, options.ReadyChildren)) { SingleReader = false, SingleWriter = true });
        this.running = new SemaphoreSlim(Math.Max(1, options.Running));
    }

    /// <summary>Jobs done since start.</summary>
    public long Jobs => Interlocked.Read(ref this.jobs);

    /// <summary>Children stopped at their deadline.</summary>
    public long KilledByDeadline => Interlocked.Read(ref this.byDeadline);

    /// <summary>Children stopped for their memory.</summary>
    public long KilledByMemory => Interlocked.Read(ref this.byMemory);

    private long jobs;
    private long byDeadline;
    private long byMemory;

    /// <summary>Starts listening and keeping children ready; returns once the socket is open.</summary>
    public void Start()
    {
        string path = this.options.SocketPath;
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        this.listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        this.listener.Bind(new UnixDomainSocketEndPoint(path));
        if (!OperatingSystem.IsWindows())
        {
            // Readable and writable by the viewer's own user and its group (the webmail's user is in it), no one else.
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        }
        this.listener.Listen(64);
        this.filler = Task.Run(() => this.FillAsync(this.stopping.Token));
        if (this.options.ParentProcessId is int parent)
        {
            _ = Task.Run(() => this.WatchParentAsync(parent, this.stopping.Token));
        }
    }

    /// <summary>Serves until stopped.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task RunAsync(CancellationToken ct)
    {
        if (this.listener is null)
        {
            this.Start();
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, this.stopping.Token);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                Socket client = await this.listener!.AcceptAsync(linked.Token).ConfigureAwait(false);
                _ = Task.Run(() => this.ServeAsync(client, linked.Token), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException) when (linked.IsCancellationRequested)
        {
        }
    }

    /// <summary>Stops listening and every child.</summary>
    /// <returns>A task.</returns>
    public async ValueTask DisposeAsync()
    {
        await this.stopping.CancelAsync().ConfigureAwait(false);
        this.listener?.Dispose();
        if (this.filler is not null)
        {
            try
            {
                await this.filler.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        while (this.ready.Reader.TryRead(out ChildProcess? child))
        {
            child.Dispose();
        }
        try
        {
            if (File.Exists(this.options.SocketPath))
            {
                File.Delete(this.options.SocketPath);
            }
        }
        catch (IOException)
        {
        }
        this.running.Dispose();
        this.stopping.Dispose();
    }

    // One connection: one request, one answer.
    private async Task ServeAsync(Socket client, CancellationToken ct)
    {
        using (client)
        {
            await using var stream = new NetworkStream(client, ownsSocket: false);
            ViewAnswer answer;
            List<byte[]> files = new();
            (string Json, List<byte[]> Files)? frame;
            try
            {
                frame = await ViewFrames.ReadAsync(stream, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or SocketException)
            {
                return;
            }
            if (frame is null)
            {
                return;
            }
            ViewRequest request;
            try
            {
                request = ViewJson.ReadRequest(frame.Value.Json);
            }
            catch (System.Text.Json.JsonException)
            {
                return;
            }
            string who = string.IsNullOrEmpty(request.Who) ? "-" : request.Who;
            if (!this.Admit(who))
            {
                answer = new ViewAnswer { Status = ViewStatus.Busy };
            }
            else
            {
                try
                {
                    (answer, files) = await this.RunJobAsync(frame.Value.Json, frame.Value.Files, request, ct).ConfigureAwait(false);
                }
                finally
                {
                    this.Leave(who);
                }
            }
            try
            {
                await ViewFrames.WriteAsync(stream, ViewJson.Write(answer), files.Select(f => (ReadOnlyMemory<byte>)f).ToList(), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidDataException)
            {
            }
        }
    }

    private bool Admit(string who)
    {
        lock (this.countLock)
        {
            this.perPerson.TryGetValue(who, out int mine);
            if (mine >= this.options.MaxPerPerson || this.waiting >= this.options.MaxWaiting)
            {
                return false;
            }
            this.perPerson[who] = mine + 1;
            this.waiting++;
            return true;
        }
    }

    private void Leave(string who)
    {
        lock (this.countLock)
        {
            if (this.perPerson.TryGetValue(who, out int mine))
            {
                if (mine <= 1)
                {
                    this.perPerson.TryRemove(who, out _);
                }
                else
                {
                    this.perPerson[who] = mine - 1;
                }
            }
            this.waiting--;
        }
    }

    private async Task<(ViewAnswer, List<byte[]>)> RunJobAsync(string json, List<byte[]> files, ViewRequest request, CancellationToken ct)
    {
        await this.running.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ChildProcess child = await this.ready.Reader.ReadAsync(ct).ConfigureAwait(false);
            using (child)
            {
                (ViewAnswer answer, List<byte[]> outFiles, ChildEnd end) = await child.RunAsync(json, files, ViewKinds.Deadline(request.Kind, request.Page), this.options.ChildMemoryLimit, ct).ConfigureAwait(false);
                Interlocked.Increment(ref this.jobs);
                if (end == ChildEnd.Deadline)
                {
                    Interlocked.Increment(ref this.byDeadline);
                    this.Log("stopped a " + request.Kind + " job at its deadline");
                }
                else if (end == ChildEnd.Memory)
                {
                    Interlocked.Increment(ref this.byMemory);
                    this.Log("stopped a " + request.Kind + " job past " + (this.options.ChildMemoryLimit / (1024 * 1024)) + " MB");
                }
                return (answer, outFiles);
            }
        }
        finally
        {
            this.running.Release();
        }
    }

    // Keeps the pool of warmed children full: one is started as soon as one is taken.
    private async Task FillAsync(CancellationToken ct)
    {
        TimeSpan pause = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            ChildProcess? child = await ChildProcess.StartAsync(this.options.Child, this.options.ReadyWait, this.options.Log, ct).ConfigureAwait(false);
            if (child is null)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }
                this.Log("a child process did not start; trying again in " + (int)pause.TotalSeconds + " s");
                try
                {
                    await Task.Delay(pause, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                pause = TimeSpan.FromSeconds(Math.Min(30, pause.TotalSeconds * 2));
                continue;
            }
            pause = TimeSpan.FromSeconds(1);
            try
            {
                await this.ready.Writer.WriteAsync(child, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                child.Dispose();
                return;
            }
        }
    }

    private async Task WatchParentAsync(int parent, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(parent);
                if (p.HasExited)
                {
                    break;
                }
            }
            catch (ArgumentException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }
            try
            {
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        await this.stopping.CancelAsync().ConfigureAwait(false);
        this.listener?.Dispose();
    }

    private void Log(string line)
    {
        lock (this.options.Log)
        {
            this.options.Log.WriteLine(ChildHost.LogPrefix + line);
        }
    }
}
