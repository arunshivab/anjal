using Anjal.Viewing;

namespace Anjal.Viewer.Tests;

/// <summary>
/// rc.29 (DES-22 §4.1, §13, D-358): the viewer as it runs - a supervisor on its socket, single-use child processes,
/// a job stopped at its deadline, a child stopped for its memory, and the next job in a fresh child.
/// </summary>
[Collection("Viewer hooks")]
public sealed class SupervisorTests : IAsyncLifetime, IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "av-" + Guid.NewGuid().ToString("N")[..8]);
    private ViewerSupervisor? supervisor;
    private Task? serving;
    private readonly CancellationTokenSource stop = new();

    private string Socket => Path.Combine(this.folder, "v.sock");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(this.folder);
        // Children get the test-only jobs (a hang, a growth); a server never sets this.
        Environment.SetEnvironmentVariable("ANJAL_VIEWER_TEST_HOOKS", "1");
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        this.supervisor = new ViewerSupervisor(new SupervisorOptions
        {
            SocketPath = this.Socket,
            ReadyChildren = 2,
            Running = 2,
            MaxPerPerson = 4,
            ChildMemoryLimit = 300L * 1024 * 1024,
            Child = new ChildCommand(host, new[] { Path.Combine(AppContext.BaseDirectory, "Anjal.Viewer.dll"), "--child" }),
            Log = TextWriter.Null,
        });
        this.supervisor.Start();
        this.serving = this.supervisor.RunAsync(this.stop.Token);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await this.stop.CancelAsync();
        if (this.supervisor is not null)
        {
            await this.supervisor.DisposeAsync();
        }
        if (this.serving is not null)
        {
            await this.serving;
        }
        TestFolders.Delete(this.folder);
    }

    public void Dispose() => this.stop.Dispose();

    [Fact]
    public async Task APdf_IsDrawnByAChildProcess_ThroughTheSocket()
    {
        var client = new ViewerClient(this.Socket);
        byte[] pdf = TestFiles.Pdf("Through the socket");
        ViewResult info = await client.AskAsync(new ViewRequest { Kind = ViewKinds.PdfInfo, Who = "a" }, pdf);
        Assert.Equal(ViewStatus.Ok, info.Answer.Status);
        Assert.Equal(1, ViewJson.ReadPdfInfo(info.Answer.Json)!.PageCount);
        ViewResult page = await client.AskAsync(new ViewRequest { Kind = ViewKinds.PdfPage, Width = 300, Who = "a" }, pdf);
        Assert.Equal("image/png", FileKinds.PictureType(page.Files[0]));
    }

    [Fact]
    public async Task ADicomGrouping_RunsInAChildOfItsOwn_BesideAnotherJob()
    {
        // rc.30 (AC-19): the grouping's child is started for it with a 1 GiB heap; the others keep 512 MB.
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var command = new ChildCommand(host, new[] { Path.Combine(AppContext.BaseDirectory, "Anjal.Viewer.dll"), "--child" });
        foreach ((long limit, long want) in new[] { (ChildProcess.DefaultHeapLimit, 512L), (1024L * 1024 * 1024, 1024L) })
        {
            using ChildProcess? child = await ChildProcess.StartAsync(command, TimeSpan.FromSeconds(60), TextWriter.Null, limit, CancellationToken.None);
            Assert.NotNull(child);
            (ViewAnswer heap, _, _) = await child.RunAsync(ViewJson.Write(new ViewRequest { Kind = "test.heap" }), new[] { Array.Empty<byte>() }, TimeSpan.FromSeconds(20), 2048L * 1024 * 1024, CancellationToken.None);
            long bytes = System.Text.Json.JsonDocument.Parse(heap.Json!).RootElement.GetProperty("heap").GetInt64();
            Assert.Equal(want, bytes / (1024 * 1024));
        }

        var client = new ViewerClient(this.Socket);
        byte[] zip = TestFiles.ZipBytes(("a.dcm", TestDicom.Slice("1", 1, 0)), ("b.dcm", TestDicom.Slice("1", 2, 5)));
        Task<ViewResult> grouping = client.AskAsync(new ViewRequest { Kind = ViewKinds.DicomGroup, Who = "g" }, zip);
        Task<ViewResult> other = client.AskAsync(new ViewRequest { Kind = ViewKinds.PdfInfo, Who = "h" }, TestFiles.Pdf("Beside it"));
        ViewResult grouped = await grouping;
        Assert.Equal(ViewStatus.Ok, grouped.Answer.Status);
        Assert.Equal(2, ViewJson.ReadStudy(grouped.Answer.Json)!.Patients.Single().Studies.Single().Series.Single().Slices);
        Assert.Equal(ViewStatus.Ok, (await other).Answer.Status);
        Assert.Equal(15, ViewKinds.Deadline(ViewKinds.DicomGroup).TotalSeconds);
    }

    [Fact]
    public async Task AJobThatHangs_IsStoppedAtItsDeadline_AndTheNextRunsInAFreshChild()
    {
        var client = new ViewerClient(this.Socket);
        var started = System.Diagnostics.Stopwatch.StartNew();
        ViewResult hung = await client.AskAsync(new ViewRequest { Kind = "test.hang", Who = "b" }, Array.Empty<byte>());
        Assert.Equal(ViewStatus.TooSlow, hung.Answer.Status);
        Assert.InRange(started.Elapsed.TotalSeconds, 2.5, 30);
        Assert.Equal(1, this.supervisor!.KilledByDeadline);
        ViewResult next = await client.AskAsync(new ViewRequest { Kind = ViewKinds.PdfInfo, Who = "b" }, TestFiles.Pdf("After the hang"));
        Assert.Equal(ViewStatus.Ok, next.Answer.Status);
    }

    [Fact]
    public async Task AChildThatGrows_IsStoppedForItsMemory()
    {
        var client = new ViewerClient(this.Socket);
        ViewResult grown = await client.AskAsync(new ViewRequest { Kind = "test.grow", Who = "c" }, Array.Empty<byte>());
        Assert.Contains(grown.Answer.Status, new[] { ViewStatus.TooLarge, ViewStatus.TooSlow, ViewStatus.Failed });
        Assert.True(this.supervisor!.KilledByMemory + this.supervisor.KilledByDeadline >= 1);
    }

    [Fact]
    public async Task OnePerson_GetsFourJobsAtOnce_AndAFifthIsTold()
    {
        var client = new ViewerClient(this.Socket);
        Task<ViewResult>[] jobs = Enumerable.Range(0, 5).Select(_ => client.AskAsync(new ViewRequest { Kind = "test.hang", Who = "same" }, Array.Empty<byte>())).ToArray();
        ViewResult[] results = await Task.WhenAll(jobs);
        Assert.Contains(results, r => r.Answer.Status == ViewStatus.Busy);
    }

    [Fact]
    public async Task NoViewer_IsSaidPlainly()
    {
        var client = new ViewerClient(Path.Combine(this.folder, "nobody.sock"));
        ViewResult r = await client.AskAsync(new ViewRequest { Kind = ViewKinds.PdfInfo }, TestFiles.Pdf("x"));
        Assert.Equal(ViewStatus.Unavailable, r.Answer.Status);
    }
}
