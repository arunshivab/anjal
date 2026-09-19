using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Server.Tests;

public class MailboxAuthAndResolverTests
{
    private static readonly string[] NoDomains = System.Array.Empty<string>();
    private static readonly string[] SigmaDomains = new[] { "sigma.test" };
    private static readonly string[] EnvDomains = new[] { "env.test" };

    private static async System.Threading.Tasks.Task<InMemoryMessageStore> SeedAsync()
    {
        var store = new InMemoryMessageStore();
        TenantRow tenant = await store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa" }).ConfigureAwait(false);
        await store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" }).ConfigureAwait(false);
        await store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.co.in",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("mailbox-secret"),
        }).ConfigureAwait(false);
        await store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = "noreply",
            Domain = "anjal.co.in",
        }).ConfigureAwait(false);
        await store.UpsertSmtpUserAsync(new SmtpUserRow
        {
            Username = "svc",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("service-secret"),
            AllowedFromDomains = SigmaDomains,
        }).ConfigureAwait(false);
        return store;
    }

    [Fact]
    public async System.Threading.Tasks.Task Auth_MailboxAddress_CorrectPassword_AllowsOwnDomainOnly()
    {
        InMemoryMessageStore store = await SeedAsync();
        var auth = new ServerSmtpAuthenticator(string.Empty, string.Empty, NoDomains, store, store);

        AuthenticatedUser? user = await auth.AuthenticateAsync("Arun@Anjal.CO.IN", "mailbox-secret");
        Assert.NotNull(user);
        Assert.Equal("arun@anjal.co.in", user!.Username);
        Assert.Single(user.AllowedFromDomains);
        Assert.Contains("anjal.co.in", user.AllowedFromDomains);
    }

    [Fact]
    public async System.Threading.Tasks.Task Auth_MailboxAddress_WrongPassword_Fails()
    {
        InMemoryMessageStore store = await SeedAsync();
        var auth = new ServerSmtpAuthenticator(string.Empty, string.Empty, NoDomains, store, store);
        Assert.Null(await auth.AuthenticateAsync("arun@anjal.co.in", "nope"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Auth_ReceiveOnlyMailbox_Fails()
    {
        InMemoryMessageStore store = await SeedAsync();
        var auth = new ServerSmtpAuthenticator(string.Empty, string.Empty, NoDomains, store, store);
        Assert.Null(await auth.AuthenticateAsync("noreply@anjal.co.in", string.Empty));
        Assert.Null(await auth.AuthenticateAsync("noreply@anjal.co.in", "anything"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Auth_DisabledTenant_Fails()
    {
        InMemoryMessageStore store = await SeedAsync();
        await store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", Enabled = false });
        var auth = new ServerSmtpAuthenticator(string.Empty, string.Empty, NoDomains, store, store);
        Assert.Null(await auth.AuthenticateAsync("arun@anjal.co.in", "mailbox-secret"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Auth_SmtpUser_StillWorks_AndDoesNotFallThroughToMailbox()
    {
        InMemoryMessageStore store = await SeedAsync();
        var auth = new ServerSmtpAuthenticator(string.Empty, string.Empty, NoDomains, store, store);

        AuthenticatedUser? svc = await auth.AuthenticateAsync("svc", "service-secret");
        Assert.NotNull(svc);
        Assert.Contains("sigma.test", svc!.AllowedFromDomains);

        // A service account with the same name as a mailbox address wins and
        // its wrong password must not be retried against the mailbox hash.
        await store.UpsertSmtpUserAsync(new SmtpUserRow
        {
            Username = "arun@anjal.co.in",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("other"),
        });
        Assert.Null(await auth.AuthenticateAsync("arun@anjal.co.in", "mailbox-secret"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Auth_WithoutMailboxStore_IgnoresMailboxes()
    {
        InMemoryMessageStore store = await SeedAsync();
        var auth = new ServerSmtpAuthenticator(string.Empty, string.Empty, NoDomains, store);
        Assert.Null(await auth.AuthenticateAsync("arun@anjal.co.in", "mailbox-secret"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Resolver_TenantDomain_IsLocal()
    {
        InMemoryMessageStore store = await SeedAsync();
        var resolver = new ServerLocalDomainResolver(NoDomains, store, store);

        Assert.True(await resolver.IsLocalAsync("anjal.co.in"));
        Assert.True(await resolver.IsLocalAsync("ANJAL.CO.IN"));
        Assert.False(await resolver.IsLocalAsync("gmail.com"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Resolver_ChainsEnvLocalDomainsAndTenantDomains()
    {
        InMemoryMessageStore store = await SeedAsync();
        await store.UpsertLocalDomainAsync("legacy.test");
        var resolver = new ServerLocalDomainResolver(EnvDomains, store, store);

        Assert.True(await resolver.IsLocalAsync("env.test"));
        Assert.True(await resolver.IsLocalAsync("legacy.test"));
        Assert.True(await resolver.IsLocalAsync("anjal.co.in"));
        Assert.False(await resolver.IsLocalAsync(string.Empty));
    }

    [Fact]
    public async System.Threading.Tasks.Task Resolver_WithoutMailboxStore_IgnoresTenantDomains()
    {
        InMemoryMessageStore store = await SeedAsync();
        var resolver = new ServerLocalDomainResolver(NoDomains, store);
        Assert.False(await resolver.IsLocalAsync("anjal.co.in"));
    }
}
