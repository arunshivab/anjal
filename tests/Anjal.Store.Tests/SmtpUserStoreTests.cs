namespace Anjal.Store.Tests;

public class SmtpUserStoreTests
{
    private static readonly string[] HospitalA = new[] { "hospital-a.test" };
    private static readonly string[] NewTestDomain = new[] { "new.test" };

    [Fact]
    public async System.Threading.Tasks.Task Upsert_Insert_AssignsId()
    {
        var store = new InMemoryMessageStore();
        var saved = await store.UpsertSmtpUserAsync(new SmtpUserRow
        {
            Username = "alice",
            PasswordPbkdf2 = "pbkdf2$1000$abc$def",
            AllowedFromDomains = HospitalA,
        });

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
        });

        var updated = await store.UpsertSmtpUserAsync(new SmtpUserRow
        {
            Username = "alice",
            PasswordPbkdf2 = "hash2",
            AllowedFromDomains = NewTestDomain,
        });

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
        });

        var found = await store.GetSmtpUserAsync("bob");
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
        });

        var found = await store.GetSmtpUserAsync("BOB");
        Assert.NotNull(found);
    }

    [Fact]
    public async System.Threading.Tasks.Task Get_Missing_ReturnsNull()
    {
        var store = new InMemoryMessageStore();
        var found = await store.GetSmtpUserAsync("missing");
        Assert.Null(found);
    }

    [Fact]
    public async System.Threading.Tasks.Task List_ReturnsAll()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertSmtpUserAsync(new SmtpUserRow { Username = "a", PasswordPbkdf2 = "x" });
        await store.UpsertSmtpUserAsync(new SmtpUserRow { Username = "b", PasswordPbkdf2 = "y" });

        var all = await store.ListSmtpUsersAsync();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task Delete_ExistingUser_ReturnsTrue()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertSmtpUserAsync(new SmtpUserRow { Username = "alice", PasswordPbkdf2 = "x" });

        bool removed = await store.DeleteSmtpUserAsync("alice");
        Assert.True(removed);
        Assert.Null(await store.GetSmtpUserAsync("alice"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Delete_MissingUser_ReturnsFalse()
    {
        var store = new InMemoryMessageStore();
        bool removed = await store.DeleteSmtpUserAsync("nobody");
        Assert.False(removed);
    }
}
