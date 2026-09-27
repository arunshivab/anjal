using Anjal.Store;

namespace Anjal.Spam.Tests;

/// <summary>
/// v1.0.0-rc.7: greylisting memory in the database, with the state file as
/// its mirror and fallback (owner's decision, 27 Sep 2026).
/// </summary>
public sealed class Rc7GreylistStoreTests : System.IDisposable
{
    private readonly string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-grey-" + System.Guid.NewGuid().ToString("N"));

    public Rc7GreylistStoreTests()
    {
        System.IO.Directory.CreateDirectory(this.dir);
    }

    private string File => System.IO.Path.Combine(this.dir, "greylist.tsv");

    public void Dispose() => System.IO.Directory.Delete(this.dir, recursive: true);

    [Fact]
    public async Task Load_PrefersTheDatabase()
    {
        var store = new InMemoryMessageStore();
        var now = System.DateTimeOffset.UtcNow;
        await store.ReplaceGreylistAsync(new[]
        {
            new GreylistRow { Key = "k1", FirstSeen = now.AddHours(-2), LastSeen = now.AddHours(-1), Passed = true },
            new GreylistRow { Key = "k2", FirstSeen = now.AddHours(-2), LastSeen = now.AddHours(-1), Passed = true },
        });
        await System.IO.File.WriteAllTextAsync(this.File, $"filekey\t{now.UtcTicks}\t{now.UtcTicks}\t1\n");
        var log = new List<string>();
        var g = new Greylist(new GreylistOptions { StateFile = this.File, LoadFromStore = store.ListGreylistAsync, SaveToStore = store.ReplaceGreylistAsync, Log = log.Add });
        Assert.Equal(2, g.Count);
        Assert.Contains(log, l => l.Contains("from the database", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task Load_FallsBackToTheFile_WhenTheDatabaseIsEmpty()
    {
        var store = new InMemoryMessageStore();
        var now = System.DateTimeOffset.UtcNow;
        await System.IO.File.WriteAllTextAsync(this.File, $"filekey\t{now.UtcTicks}\t{now.UtcTicks}\t1\n");
        var log = new List<string>();
        var g = new Greylist(new GreylistOptions { StateFile = this.File, LoadFromStore = store.ListGreylistAsync, Log = log.Add });
        Assert.Equal(1, g.Count);
        Assert.Contains(log, l => l.Contains("holds no remembered senders; trying the file", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task Load_FallsBackToTheFile_WhenTheDatabaseCannotBeRead()
    {
        var now = System.DateTimeOffset.UtcNow;
        await System.IO.File.WriteAllTextAsync(this.File, $"filekey\t{now.UtcTicks}\t{now.UtcTicks}\t1\n");
        var log = new List<string>();
        var g = new Greylist(new GreylistOptions
        {
            StateFile = this.File,
            LoadFromStore = _ => throw new System.InvalidOperationException("connection refused"),
            Log = log.Add,
        });
        Assert.Equal(1, g.Count);
        Assert.Contains(log, l => l.Contains("could not read the database (connection refused); trying the file", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flush_WritesBothTheDatabaseAndTheFile()
    {
        var store = new InMemoryMessageStore();
        var g = new Greylist(new GreylistOptions { StateFile = this.File, LoadFromStore = store.ListGreylistAsync, SaveToStore = store.ReplaceGreylistAsync });
        g.OnRcptTo("203.0.113.9", null, "sender@example.com", "arun@anjal.co.in");
        g.Flush();
        Assert.Single(await store.ListGreylistAsync());
        Assert.Single(await System.IO.File.ReadAllLinesAsync(this.File));
    }
}
