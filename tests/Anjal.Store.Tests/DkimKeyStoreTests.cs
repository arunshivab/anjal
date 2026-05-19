namespace Anjal.Store.Tests;

public class DkimKeyStoreTests
{
    [Fact]
    public async System.Threading.Tasks.Task Upsert_AssignsIdAndUpdatedAt()
    {
        var store = new InMemoryMessageStore();
        DkimKeyRow saved = await store.UpsertDkimKeyAsync(new DkimKeyRow
        {
            Domain = "mail.lipi.in",
            Selector = "default",
            PrivateKeyPem = "PEM-PLACEHOLDER",
        });

        Assert.NotEqual(System.Guid.Empty, saved.Id);
        Assert.Equal("mail.lipi.in", saved.Domain);
        Assert.Equal("default", saved.Selector);
        Assert.Equal("PEM-PLACEHOLDER", saved.PrivateKeyPem);
        Assert.NotEqual(default, saved.UpdatedAt);
    }

    [Fact]
    public async System.Threading.Tasks.Task Upsert_DomainIsLowercased()
    {
        var store = new InMemoryMessageStore();
        DkimKeyRow saved = await store.UpsertDkimKeyAsync(new DkimKeyRow
        {
            Domain = "Mail.LIPI.in",
            Selector = "default",
            PrivateKeyPem = "x",
        });
        Assert.Equal("mail.lipi.in", saved.Domain);
    }

    [Fact]
    public async System.Threading.Tasks.Task Upsert_RotatesExisting()
    {
        var store = new InMemoryMessageStore();
        DkimKeyRow first = await store.UpsertDkimKeyAsync(new DkimKeyRow
        {
            Domain = "x.test",
            Selector = "old",
            PrivateKeyPem = "OLD-PEM",
        });

        DkimKeyRow second = await store.UpsertDkimKeyAsync(new DkimKeyRow
        {
            Domain = "x.test",
            Selector = "new",
            PrivateKeyPem = "NEW-PEM",
        });

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("new", second.Selector);
        Assert.Equal("NEW-PEM", second.PrivateKeyPem);
    }

    [Fact]
    public async System.Threading.Tasks.Task Get_CaseInsensitive()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertDkimKeyAsync(new DkimKeyRow { Domain = "x.test", Selector = "s", PrivateKeyPem = "p" });
        Assert.NotNull(await store.GetDkimKeyAsync("x.test"));
        Assert.NotNull(await store.GetDkimKeyAsync("X.TEST"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Get_Missing_ReturnsNull()
    {
        var store = new InMemoryMessageStore();
        Assert.Null(await store.GetDkimKeyAsync("nope.test"));
    }

    [Fact]
    public async System.Threading.Tasks.Task List_ReturnsAll()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertDkimKeyAsync(new DkimKeyRow { Domain = "a.test", Selector = "s", PrivateKeyPem = "p" });
        await store.UpsertDkimKeyAsync(new DkimKeyRow { Domain = "b.test", Selector = "s", PrivateKeyPem = "p" });
        var all = await store.ListDkimKeysAsync();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task Delete_RemovesExisting()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertDkimKeyAsync(new DkimKeyRow { Domain = "gone.test", Selector = "s", PrivateKeyPem = "p" });
        Assert.True(await store.DeleteDkimKeyAsync("gone.test"));
        Assert.False(await store.DeleteDkimKeyAsync("gone.test"));
        Assert.Null(await store.GetDkimKeyAsync("gone.test"));
    }
}
