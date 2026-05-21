namespace Anjal.Store.Tests;

public class LocalDomainStoreTests
{
    [Fact]
    public async System.Threading.Tasks.Task Upsert_Insert_AssignsId()
    {
        var store = new InMemoryMessageStore();
        var saved = await store.UpsertLocalDomainAsync("hospital-a.test");

        Assert.NotEqual(System.Guid.Empty, saved.Id);
        Assert.Equal("hospital-a.test", saved.Domain);
    }

    [Fact]
    public async System.Threading.Tasks.Task Upsert_LowercasesDomain()
    {
        var store = new InMemoryMessageStore();
        var saved = await store.UpsertLocalDomainAsync("Hospital-A.TEST");
        Assert.Equal("hospital-a.test", saved.Domain);
    }

    [Fact]
    public async System.Threading.Tasks.Task Upsert_Duplicate_Idempotent()
    {
        var store = new InMemoryMessageStore();
        var first = await store.UpsertLocalDomainAsync("x.test");
        var second = await store.UpsertLocalDomainAsync("x.test");
        Assert.Equal(first.Id, second.Id);

        var all = await store.ListLocalDomainsAsync();
        Assert.Single(all);
    }

    [Fact]
    public async System.Threading.Tasks.Task IsLocal_KnownDomain_ReturnsTrue()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertLocalDomainAsync("hospital-a.test");
        Assert.True(await store.IsLocalDomainAsync("hospital-a.test"));
    }

    [Fact]
    public async System.Threading.Tasks.Task IsLocal_CaseInsensitive()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertLocalDomainAsync("hospital-a.test");
        Assert.True(await store.IsLocalDomainAsync("HOSPITAL-A.TEST"));
    }

    [Fact]
    public async System.Threading.Tasks.Task IsLocal_UnknownDomain_ReturnsFalse()
    {
        var store = new InMemoryMessageStore();
        Assert.False(await store.IsLocalDomainAsync("nobody.test"));
    }

    [Fact]
    public async System.Threading.Tasks.Task List_ReturnsAll()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertLocalDomainAsync("a.test");
        await store.UpsertLocalDomainAsync("b.test");

        var all = await store.ListLocalDomainsAsync();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task Delete_Existing_ReturnsTrue()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertLocalDomainAsync("doomed.test");
        bool removed = await store.DeleteLocalDomainAsync("doomed.test");
        Assert.True(removed);
        Assert.False(await store.IsLocalDomainAsync("doomed.test"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Delete_Missing_ReturnsFalse()
    {
        var store = new InMemoryMessageStore();
        bool removed = await store.DeleteLocalDomainAsync("nothing.test");
        Assert.False(removed);
    }
}
