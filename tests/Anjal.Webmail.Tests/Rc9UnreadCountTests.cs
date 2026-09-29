using System.Net;
using System.Text.RegularExpressions;
using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Anjal.Webmail.Tests;

/// <summary>
/// v1.0.0-rc.9 (DEF-066, D-56): opening an unread message lowers the INBOX
/// unread count on that same page, as every mail program does - not only on the
/// next page.
/// </summary>
public sealed partial class Rc9UnreadCountTests : IAsyncLifetime, IDisposable
{
    private static readonly string[] Arun = { "arun@anjal.co.in" };
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc9-unread-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private WebApplication app = null!;
    private HttpClient client = null!;

    [GeneratedRegex("data-unread-badge=\"1\">(\\d+)</span>")]
    private static partial Regex InboxBadge();

    public void Dispose() => this.client?.Dispose();

    public async Task InitializeAsync()
    {
        var maildir = new MaildirStore(Path.Combine(this.root, "mail"), "test");
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "arun", Domain = "anjal.co.in", PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000) });
        var sink = new MailboxSink(this.store, maildir);
        foreach (string subject in new[] { "First", "Second" })
        {
            await sink.DeliverAsync(new DeliveryContext { EnvelopeFrom = "s@x.test", EnvelopeTo = Arun, RawBytes = System.Text.Encoding.ASCII.GetBytes($"From: s@x.test\r\nSubject: {subject}\r\n\r\nbody\r\n") });
        }
        this.app = Program.CreateApp(Array.Empty<string>(), this.store, maildir, "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
        string address = this.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        this.client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(address + "/") };
        string html = await (await this.client.GetAsync("sign-in")).Content.ReadAsStringAsync();
        HttpResponseMessage login = await this.client.PostAsync("auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = TokenRegex.Match(html).Groups[1].Value,
            ["address"] = "arun@anjal.co.in",
            ["password"] = "correct horse battery",
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
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

    /// <summary>The INBOX unread count on a page; the badge is absent when it is 0.</summary>
    private async Task<int> BadgeOnAsync(string path)
    {
        Match m = InboxBadge().Match(await this.client.GetStringAsync(path));
        return m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    [Fact]
    public async Task OpeningAnUnreadMessage_LowersTheCount_OnThatSamePage()
    {
        Assert.Equal(2, await this.BadgeOnAsync(string.Empty));
        Guid first = this.store.MailboxMessages.Single(m => m.Subject == "First").Id;
        Assert.Equal(1, await this.BadgeOnAsync($"message/{first}"));                 // at once, not on the next page
        Guid second = this.store.MailboxMessages.Single(m => m.Subject == "Second").Id;
        Assert.Equal(0, await this.BadgeOnAsync($"message/{second}"));
    }

    [Fact]
    public async Task OpeningAMessageAlreadyRead_LeavesTheCountAlone()
    {
        Guid first = this.store.MailboxMessages.Single(m => m.Subject == "First").Id;
        await this.client.GetStringAsync($"message/{first}");
        Assert.Equal(1, await this.BadgeOnAsync($"message/{first}"));
    }
}
