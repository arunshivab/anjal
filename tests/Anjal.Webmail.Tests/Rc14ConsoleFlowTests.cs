using System.Net;
using System.Text.RegularExpressions;
using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Anjal.Webmail.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Anjal.Webmail.Tests;

/// <summary>
/// rc.14 through the real pipeline: the organisation console for its
/// administrators only, invitation links shown once, disabling a person
/// signing them out, the activity log and its chain, shared mailboxes
/// opened with their rights, required two-step, and the Anjal console for
/// operators only.
/// </summary>
[Collection("Operators")]
public sealed class Rc14ConsoleFlowTests : IAsyncLifetime, IDisposable
{
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly Regex InviteRegex = new("/invite/([A-Za-z0-9_-]{20,})", RegexOptions.CultureInvariant);

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc14e-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly List<HttpClient> clients = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private string baseAddress = string.Empty;
    private TenantRow tenant = new();
    private MailboxRow arun = new();
    private MailboxRow meera = new();

    public void Dispose()
    {
        foreach (HttpClient c in this.clients)
        {
            c.Dispose();
        }
    }

    public async Task InitializeAsync()
    {
        this.maildir = new MaildirStore(this.root, "test");
        this.tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "Imagiqa", PostmasterMailbox = "arun@anjal.co.in" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.tenant.Id, Domain = "anjal.co.in" });
        this.arun = await this.Person("arun", "Arun Shiva B");
        this.meera = await this.Person("meera.iyer", "Meera Iyer");
        this.app = Program.CreateApp(Array.Empty<string>(), this.store, this.maildir, "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
        this.baseAddress = this.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        await this.app.StopAsync();
        await this.app.DisposeAsync();
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    [Fact]
    public async Task OrganisationConsole_IsForItsAdministratorsOnly()
    {
        HttpClient member = this.Client();
        await SignInAsync(member, "meera.iyer", "correct horse battery");
        string page = await member.GetStringAsync("org/people");
        Assert.Contains("Only an administrator of your organisation can open this.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Invite people", page, StringComparison.Ordinal);
        string token = Token(await member.GetStringAsync("settings"));
        Assert.Equal(HttpStatusCode.NotFound, (await Post(member, "org/people/invite", token, ("name", "X"), ("local", "x"), ("domain", "anjal.co.in"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(member, "org/signin", token, ("minLength", "12"), ("history", "0"), ("sharedIdle", "15"), ("ownIdle", "8"), ("stay", "30"), ("trust", "30"))).StatusCode);

        HttpClient admin = this.Client();
        await SignInAsync(admin, "arun", "correct horse battery");
        string people = await admin.GetStringAsync("org/people");
        Assert.Contains("Invite people", people, StringComparison.Ordinal);
        Assert.Contains("meera.iyer@anjal.co.in", people, StringComparison.Ordinal);
        Assert.Contains("href=\"/org\"", await admin.GetStringAsync("folder/INBOX"), StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/org\"", await member.GetStringAsync("folder/INBOX"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invite_ShowsTheLinkOnce_AndTheLinkLetsThePersonIn()
    {
        HttpClient admin = this.Client();
        await SignInAsync(admin, "arun", "correct horse battery");
        string token = Token(await admin.GetStringAsync("org/people?invite=1"));
        HttpResponseMessage res = await Post(admin, "org/people/invite", token, ("name", "Rahul Desai"), ("local", "rahul.desai"), ("domain", "anjal.co.in"));
        Assert.Equal("/org/people?links=1", res.Headers.Location!.ToString());
        string shown = await admin.GetStringAsync("org/people?links=1");
        Match link = InviteRegex.Match(shown);
        Assert.True(link.Success);
        Assert.DoesNotMatch(InviteRegex, await admin.GetStringAsync("org/people?links=1"));

        HttpClient rahul = this.Client();
        string invite = await rahul.GetStringAsync("invite/" + link.Groups[1].Value);
        Assert.Contains("rahul.desai@anjal.co.in", invite, StringComparison.Ordinal);

        string csv = await admin.GetStringAsync("org/log.csv");
        Assert.StartsWith("time,who,action,on,from,result,detail,entry,chain", csv, StringComparison.Ordinal);
        Assert.Contains("Invited a person,rahul.desai@anjal.co.in", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabling_SignsThePersonOutEverywhere_AndKeepsTheirMail()
    {
        HttpClient member = this.Client();
        await SignInAsync(member, "meera.iyer", "correct horse battery");
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("folder/INBOX")).StatusCode);

        HttpClient admin = this.Client();
        await SignInAsync(admin, "arun", "correct horse battery");
        string token = Token(await admin.GetStringAsync("org/people?sel=" + this.meera.Id));
        HttpResponseMessage res = await Post(admin, $"org/people/{this.meera.Id}/disable", token);
        Assert.Contains("saved=disable", res.Headers.Location!.ToString(), StringComparison.Ordinal);

        HttpResponseMessage after = await member.GetAsync("folder/INBOX");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Contains("sign-in", after.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.NotNull(await this.store.GetMailboxByIdAsync(this.meera.Id));

        // An administrator cannot disable themselves.
        res = await Post(admin, $"org/people/{this.arun.Id}/disable", token);
        Assert.Contains("e=", res.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.True((await this.store.GetMailboxByIdAsync(this.arun.Id))!.Enabled);
    }

    [Fact]
    public async Task ActivityLog_ListsAdministratorActions_AndItsChainChecks()
    {
        HttpClient admin = this.Client();
        await SignInAsync(admin, "arun", "correct horse battery");
        string token = Token(await admin.GetStringAsync("org/retention"));
        HttpResponseMessage saved = await Post(admin, "org/retention", token, ("trash", "14"), ("junk", "30"), ("outbox", "7"), ("apps", "7"), ("evidence", "5"));
        Assert.Equal("/org/retention?saved=1", saved.Headers.Location!.ToString());
        string log = await admin.GetStringAsync("org/log?kind=admin");
        Assert.Contains("Changed retention", log, StringComparison.Ordinal);
        Assert.DoesNotContain(">Signed in<", log, StringComparison.Ordinal);
        HttpResponseMessage verified = await Post(admin, "org/log/verify", Token(log));
        Assert.StartsWith("/org/log?chain=ok", verified.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Contains("The chain is intact", await admin.GetStringAsync(verified.Headers.Location!.ToString().TrimStart('/')), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedMailbox_OpensWithItsRight_AndReadOnlyCannotChangeIt()
    {
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        (string? _, Guid? ops) = await svc.CreateSharedMailboxAsync(this.tenant, "operations@anjal.co.in", "Operations");
        Assert.Null(await svc.SetSharedRightAsync(this.tenant, ops!.Value, this.meera.Id, "read"));

        HttpClient member = this.Client();
        await SignInAsync(member, "meera.iyer", "correct horse battery");
        string inbox = await member.GetStringAsync("folder/INBOX");
        Assert.Contains("Operations", inbox, StringComparison.Ordinal);
        HttpResponseMessage opened = await Post(member, "shared/open", Token(inbox), ("id", ops.Value.ToString()));
        Assert.Equal("/folder/INBOX", opened.Headers.Location!.ToString());
        string acting = await member.GetStringAsync("folder/INBOX");
        Assert.Contains("You are in operations@anjal.co.in, with read-only rights.", acting, StringComparison.Ordinal);

        HttpResponseMessage refused = await Post(member, "folder/INBOX/readall", Token(acting));
        Assert.Equal("/folder/INBOX?readonly=1", refused.Headers.Location!.ToString());

        // Taking the right away closes the mailbox on the next page.
        Assert.Null(await svc.SetSharedRightAsync(this.tenant, ops.Value, this.meera.Id, null));
        HttpResponseMessage closed = await member.GetAsync("folder/INBOX");
        Assert.Equal("/folder/INBOX?sharedclosed=1", closed.Headers.Location!.ToString());
        string back = await member.GetStringAsync("folder/INBOX?sharedclosed=1");
        Assert.DoesNotContain("You are in operations@", back, StringComparison.Ordinal);
        Assert.Contains("That shared mailbox is no longer open to you.", back, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequiredTwoStep_SendsTheAdministratorToSetItUp()
    {
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        Assert.Null(await svc.SaveSignInPolicyAsync(this.tenant.Id, new SignInPolicy { TwoStep = "admins" }));
        HttpClient admin = this.Client();
        await SignInAsync(admin, "arun", "correct horse battery");
        HttpResponseMessage res = await admin.GetAsync("folder/INBOX");
        Assert.Equal("/settings/security/two-step?required=1", res.Headers.Location!.ToString());
        string page = await admin.GetStringAsync("settings/security/two-step?required=1");
        Assert.Contains("Your organisation requires two-step sign-in.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Skip for now", page, StringComparison.Ordinal);

        HttpClient member = this.Client();
        await SignInAsync(member, "meera.iyer", "correct horse battery");
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("folder/INBOX")).StatusCode);
    }

    [Fact]
    public async Task AnjalConsole_IsForOperatorsOnly()
    {
        string? before = Environment.GetEnvironmentVariable("ANJAL_OPERATORS");
        try
        {
            Environment.SetEnvironmentVariable("ANJAL_OPERATORS", null);
            HttpClient admin = this.Client();
            await SignInAsync(admin, "arun", "correct horse battery");
            Assert.Contains("Only an operator of this Anjal service can open this.", await admin.GetStringAsync("ops"), StringComparison.Ordinal);
            string token = Token(await admin.GetStringAsync("settings"));
            Assert.Equal(HttpStatusCode.NotFound, (await Post(admin, "ops/health/summary", token)).StatusCode);

            Environment.SetEnvironmentVariable("ANJAL_OPERATORS", "someone@else.example, Arun@Anjal.co.in");
            string orgs = await admin.GetStringAsync("ops/orgs");
            Assert.Contains("Organisations", orgs, StringComparison.Ordinal);
            Assert.Contains("Imagiqa", orgs, StringComparison.Ordinal);
            string health = await admin.GetStringAsync("ops/health");
            Assert.Contains("Delivery queue", health, StringComparison.Ordinal);
            Assert.Contains("Messages received per hour, last 24 hours", health, StringComparison.Ordinal);
            Assert.Equal(24, Regex.Count(health, "class=\"hourbar\""));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANJAL_OPERATORS", before);
        }
    }

    private static string Token(string html)
    {
        Match m = TokenRegex.Match(html);
        Assert.True(m.Success, "no antiforgery token");
        return m.Groups[1].Value;
    }

    private static Task<HttpResponseMessage> Post(HttpClient c, string path, string token, params (string Key, string Value)[] fields)
    {
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        form.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        return c.PostAsync(path, new FormUrlEncodedContent(form));
    }

    private static async Task SignInAsync(HttpClient c, string user, string password)
    {
        string token = Token(await c.GetStringAsync("sign-in"));
        HttpResponseMessage res = await Post(c, "auth/login", token, ("address", user), ("password", password));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
    }

    private Task<MailboxRow> Person(string local, string name) =>
        this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = this.tenant.Id,
            LocalPart = local,
            Domain = "anjal.co.in",
            DisplayName = name,
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        });

    private HttpClient Client()
    {
        var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(this.baseAddress + "/") };
        this.clients.Add(c);
        return c;
    }
}
