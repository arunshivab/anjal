namespace Anjal.Store.Tests;

public class SmtpUserStoreTests
{
    [Fact]
    public async System.Threading.Tasks.Task Upsert_Insert_AssignsId()
    {
        var store = new InMemoryMessageStore();
        var saved = await store.UpsertSmtpUserAsync(new SmtpUserRow
        {
            Username = "alice",
            PasswordPbkdf2 = "pbkdf2$1000$abc$def",
            AllowedFromDomains = new[] { "hospital-a.test" },
        }).ConfigureAwait(false);

        Assert.NotEqual(System.Guid.Empty, saved.Id);
        Assert.Equal("alice", saved.Username);
    }

    [Fact]
    public async System.Threading.Tasks.Task Upsert_Update_KeepsId()
    {
        var store = new InMemoryMessageStore();
        var first = await store.UpsertSmtpUserAsync(new SmtpUserRow
        {
            Username = "alice",
            PasswordPbkdf2 = "hash1",
        }).ConfigureAwait(false);

        var updated = await store.UpsertSmtpUserAsync(new SmtpUserRow
        {
            Username = "alice",
            PasswordPbkdf2 = "hash2",
            AllowedFromDomains = new[] { "new.test" },
        }).ConfigureAwait(false);

        Assert.Equal(first.Id, updated.Id);
        Assert.Equal("hash2", updated.PasswordPbkdf2);
        Assert.Single(updated.AllowedFromDomains);
    }

    [Fact]
    public async System.Threading.Tasks.Task Get_ByUsername_ReturnsRow()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertSmtpUserAsync(new SmtpUserRow
        {
            Username = "bob",
            PasswordPbkdf2 = "hash",
        }).ConfigureAwait(false);

        var found = await store.GetSmtpUserAsync("bob").ConfigureAwait(false);
        Assert.NotNull(found);
        Assert.Equal("bob", found!.Username);
    }

    [Fact]
    public async System.Threading.Tasks.Task Get_CaseInsensitive()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertSmtpUserAsync(new SmtpUserRow
        {
            Username = "Bob",
            PasswordPbkdf2 = "hash",
        }).ConfigureAwait(false);

        var found = await store.GetSmtpUserAsync("BOB").ConfigureAwait(false);
        Assert.NotNull(found);
    }

    [Fact]
    public async System.Threading.Tasks.Task Get_Missing_ReturnsNull()
    {
        var store = new InMemoryMessageStore();
        var found = await store.GetSmtpUserAsync("missing").ConfigureAwait(false);
        Assert.Null(found);
    }

    [Fact]
    public async System.Threading.Tasks.Task List_ReturnsAll()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertSmtpUserAsync(new SmtpUserRow { Username = "a", PasswordPbkdf2 = "x" }).ConfigureAwait(false);
        await store.UpsertSmtpUserAsync(new SmtpUserRow { Username = "b", PasswordPbkdf2 = "y" }).ConfigureAwait(false);

        var all = await store.ListSmtpUsersAsync().ConfigureAwait(false);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task Delete_ExistingUser_ReturnsTrue()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertSmtpUserAsync(new SmtpUserRow { Username = "alice", PasswordPbkdf2 = "x" }).ConfigureAwait(false);

        bool removed = await store.DeleteSmtpUserAsync("alice").ConfigureAwait(false);
        Assert.True(removed);
        Assert.Null(await store.GetSmtpUserAsync("alice").ConfigureAwait(false));
    }

    [Fact]
    public async System.Threading.Tasks.Task Delete_MissingUser_ReturnsFalse()
    {
        var store = new InMemoryMessageStore();
        bool removed = await store.DeleteSmtpUserAsync("nobody").ConfigureAwait(false);
        Assert.False(removed);
    }
}
