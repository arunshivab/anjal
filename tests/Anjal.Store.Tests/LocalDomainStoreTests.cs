namespace Anjal.Store.Tests;

public class LocalDomainStoreTests
{
    [Fact]
    public async System.Threading.Tasks.Task Upsert_Insert_AssignsId()
    {
        var store = new InMemoryMessageStore();
        var saved = await store.UpsertLocalDomainAsync("hospital-a.test").ConfigureAwait(false);

        Assert.NotEqual(System.Guid.Empty, saved.Id);
        Assert.Equal("hospital-a.test", saved.Domain);
    }

    [Fact]
    public async System.Threading.Tasks.Task Upsert_LowercasesDomain()
    {
        var store = new InMemoryMessageStore();
        var saved = await store.UpsertLocalDomainAsync("Hospital-A.TEST").ConfigureAwait(false);
        Assert.Equal("hospital-a.test", saved.Domain);
    }

    [Fact]
    public async System.Threading.Tasks.Task Upsert_Duplicate_Idempotent()
    {
        var store = new InMemoryMessageStore();
        var first = await store.UpsertLocalDomainAsync("x.test").ConfigureAwait(false);
        var second = await store.UpsertLocalDomainAsync("x.test").ConfigureAwait(false);
        Assert.Equal(first.Id, second.Id);

        var all = await store.ListLocalDomainsAsync().ConfigureAwait(false);
        Assert.Single(all);
    }

    [Fact]
    public async System.Threading.Tasks.Task IsLocal_KnownDomain_ReturnsTrue()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertLocalDomainAsync("hospital-a.test").ConfigureAwait(false);
        Assert.True(await store.IsLocalDomainAsync("hospital-a.test").ConfigureAwait(false));
    }

    [Fact]
    public async System.Threading.Tasks.Task IsLocal_CaseInsensitive()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertLocalDomainAsync("hospital-a.test").ConfigureAwait(false);
        Assert.True(await store.IsLocalDomainAsync("HOSPITAL-A.TEST").ConfigureAwait(false));
    }

    [Fact]
    public async System.Threading.Tasks.Task IsLocal_UnknownDomain_ReturnsFalse()
    {
        var store = new InMemoryMessageStore();
        Assert.False(await store.IsLocalDomainAsync("nobody.test").ConfigureAwait(false));
    }

    [Fact]
    public async System.Threading.Tasks.Task List_ReturnsAll()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertLocalDomainAsync("a.test").ConfigureAwait(false);
        await store.UpsertLocalDomainAsync("b.test").ConfigureAwait(false);

        var all = await store.ListLocalDomainsAsync().ConfigureAwait(false);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task Delete_Existing_ReturnsTrue()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertLocalDomainAsync("doomed.test").ConfigureAwait(false);
        bool removed = await store.DeleteLocalDomainAsync("doomed.test").ConfigureAwait(false);
        Assert.True(removed);
        Assert.False(await store.IsLocalDomainAsync("doomed.test").ConfigureAwait(false));
    }

    [Fact]
    public async System.Threading.Tasks.Task Delete_Missing_ReturnsFalse()
    {
        var store = new InMemoryMessageStore();
        bool removed = await store.DeleteLocalDomainAsync("nothing.test").ConfigureAwait(false);
        Assert.False(removed);
    }
}
