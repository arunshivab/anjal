namespace Anjal.Mailbox.Tests;

public sealed class MaildirStoreTests : System.IDisposable
{
    private static readonly byte[] Sample = System.Text.Encoding.ASCII.GetBytes("Subject: hi\r\n\r\nbody\r\n");

    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-maildir-" + System.Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (System.IO.Directory.Exists(this.root))
        {
            System.IO.Directory.Delete(this.root, recursive: true);
        }
    }

    [Fact]
    public void Ctor_RejectsEmptyRoot()
    {
        Assert.Throws<System.ArgumentException>(() => new MaildirStore("   "));
    }

    [Fact]
    public void FolderPath_InboxIsMailboxRoot_OthersAreDotFolders()
    {
        var store = new MaildirStore(this.root, "host");
        string inbox = store.FolderPath("imagiqa", "arun@anjal.co.in", Store.FolderRow.Inbox);
        string sent = store.FolderPath("imagiqa", "arun@anjal.co.in", "Sent");

        Assert.Equal(System.IO.Path.Combine(store.Root, "imagiqa", "arun@anjal.co.in"), inbox);
        Assert.Equal(System.IO.Path.Combine(inbox, ".Sent"), sent);
    }

    [Fact]
    public void FolderPath_RejectsTraversalSegments()
    {
        var store = new MaildirStore(this.root, "host");
        Assert.Throws<System.ArgumentException>(() => store.FolderPath("..", "a@b", Store.FolderRow.Inbox));
        Assert.Throws<System.ArgumentException>(() => store.FolderPath("t", "../x", Store.FolderRow.Inbox));
        Assert.Throws<System.ArgumentException>(() => store.FolderPath("t", "a@b", "x/y"));
    }

    [Fact]
    public void EnsureFolder_CreatesTmpNewCur()
    {
        var store = new MaildirStore(this.root, "host");
        string dir = store.EnsureFolder("imagiqa", "arun@anjal.co.in", "Drafts");

        Assert.True(System.IO.Directory.Exists(System.IO.Path.Combine(dir, "tmp")));
        Assert.True(System.IO.Directory.Exists(System.IO.Path.Combine(dir, "new")));
        Assert.True(System.IO.Directory.Exists(System.IO.Path.Combine(dir, "cur")));
    }

    [Fact]
    public async System.Threading.Tasks.Task Write_LandsInNew_TmpIsEmpty_AndReadsBack()
    {
        var store = new MaildirStore(this.root, "host");
        MaildirWriteResult r = await store.WriteAsync("imagiqa", "arun@anjal.co.in", Store.FolderRow.Inbox, Sample);

        Assert.StartsWith("new/", r.RelativePath, System.StringComparison.Ordinal);
        Assert.EndsWith(".host", r.RelativePath, System.StringComparison.Ordinal);
        Assert.Equal(Sample.Length, r.SizeBytes);
        Assert.True(System.IO.File.Exists(r.FullPath));

        string dir = store.FolderPath("imagiqa", "arun@anjal.co.in", Store.FolderRow.Inbox);
        Assert.Empty(System.IO.Directory.GetFiles(System.IO.Path.Combine(dir, "tmp")));

        byte[]? back = await store.ReadAsync("imagiqa", "arun@anjal.co.in", Store.FolderRow.Inbox, r.RelativePath);
        Assert.NotNull(back);
        Assert.Equal(Sample, back);
    }

    [Fact]
    public async System.Threading.Tasks.Task Write_TwiceInSameInstant_ProducesDistinctNames()
    {
        var store = new MaildirStore(this.root, "host");
        MaildirWriteResult a = await store.WriteAsync("t", "a@b", Store.FolderRow.Inbox, Sample);
        MaildirWriteResult b = await store.WriteAsync("t", "a@b", Store.FolderRow.Inbox, Sample);
        Assert.NotEqual(a.RelativePath, b.RelativePath);
    }

    [Fact]
    public async System.Threading.Tasks.Task Read_MissingOrEscapingPath_ReturnsNull()
    {
        var store = new MaildirStore(this.root, "host");
        store.EnsureFolder("t", "a@b", Store.FolderRow.Inbox);

        Assert.Null(await store.ReadAsync("t", "a@b", Store.FolderRow.Inbox, "new/does-not-exist"));
        Assert.Null(await store.ReadAsync("t", "a@b", Store.FolderRow.Inbox, "../secret"));
        Assert.Null(await store.ReadAsync("t", "a@b", Store.FolderRow.Inbox, "tmp/x"));
        Assert.Null(await store.ReadAsync("t", "a@b", Store.FolderRow.Inbox, "new/../../x"));
    }

    [Fact]
    public void FlagSuffix_OrdersFlagsAndStripFlagsInverts()
    {
        Assert.Equal(":2,", MaildirStore.FlagSuffix(false, false, false));
        Assert.Equal(":2,S", MaildirStore.FlagSuffix(true, false, false));
        Assert.Equal(":2,FRS", MaildirStore.FlagSuffix(true, true, true));
        Assert.Equal("abc.host", MaildirStore.StripFlags("abc.host:2,FS"));
        Assert.Equal("abc.host", MaildirStore.StripFlags("abc.host;2,FS"));
        Assert.Equal("abc.host", MaildirStore.StripFlags("abc.host"));
    }

    [Fact]
    public async System.Threading.Tasks.Task SetFlags_MovesNewToCur_AndRewritesSuffix()
    {
        var store = new MaildirStore(this.root, "host");
        MaildirWriteResult w = await store.WriteAsync("t", "a@b", Store.FolderRow.Inbox, Sample);

        string? seen = store.SetFlags("t", "a@b", Store.FolderRow.Inbox, w.RelativePath, seen: true, flagged: false, answered: false);
        Assert.NotNull(seen);
        Assert.StartsWith("cur/", seen, System.StringComparison.Ordinal);
        Assert.EndsWith("2,S", seen, System.StringComparison.Ordinal);
        Assert.False(System.IO.File.Exists(w.FullPath));
        Assert.Equal(Sample, await store.ReadAsync("t", "a@b", Store.FolderRow.Inbox, seen!));

        string? flagged = store.SetFlags("t", "a@b", Store.FolderRow.Inbox, seen!, seen: true, flagged: true, answered: false);
        Assert.EndsWith("2,FS", flagged, System.StringComparison.Ordinal);
        Assert.Equal(Sample, await store.ReadAsync("t", "a@b", Store.FolderRow.Inbox, flagged!));
        Assert.Null(await store.ReadAsync("t", "a@b", Store.FolderRow.Inbox, seen!));

        // Same flags again is a no-op rename.
        Assert.Equal(flagged, store.SetFlags("t", "a@b", Store.FolderRow.Inbox, flagged!, true, true, false));
        Assert.Null(store.SetFlags("t", "a@b", Store.FolderRow.Inbox, "new/missing", true, false, false));
    }

    [Fact]
    public async System.Threading.Tasks.Task Move_CrossFolder_KeepsNameAndFlags()
    {
        var store = new MaildirStore(this.root, "host");
        MaildirWriteResult w = await store.WriteAsync("t", "a@b", Store.FolderRow.Inbox, Sample);
        string seen = store.SetFlags("t", "a@b", Store.FolderRow.Inbox, w.RelativePath, true, false, false)!;

        string? moved = store.Move("t", "a@b", Store.FolderRow.Inbox, seen, "Trash");
        Assert.Equal(seen, moved);
        Assert.Null(await store.ReadAsync("t", "a@b", Store.FolderRow.Inbox, seen));
        Assert.Equal(Sample, await store.ReadAsync("t", "a@b", "Trash", moved!));
        Assert.Null(store.Move("t", "a@b", Store.FolderRow.Inbox, seen, "Trash"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Delete_RemovesFile()
    {
        var store = new MaildirStore(this.root, "host");
        MaildirWriteResult w = await store.WriteAsync("t", "a@b", Store.FolderRow.Inbox, Sample);
        Assert.True(store.Delete("t", "a@b", Store.FolderRow.Inbox, w.RelativePath));
        Assert.False(store.Delete("t", "a@b", Store.FolderRow.Inbox, w.RelativePath));
        Assert.False(store.Delete("t", "a@b", Store.FolderRow.Inbox, "../x"));
    }

    [Fact]
    public void DefaultRoot_IsPlatformSpecific()
    {
        string root = MaildirStore.DefaultRoot;
        if (System.OperatingSystem.IsWindows())
        {
            Assert.EndsWith(System.IO.Path.Combine("Anjal", "mail"), root, System.StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal("/var/mail/anjal", root);
        }
    }
}
