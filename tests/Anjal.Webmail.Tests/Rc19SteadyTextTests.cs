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
/// rc.19, DEF-106 (owner 13 Oct 2026): text that changes on an open screen never moves what is
/// beside it. The connection light keeps one width, the wider of "Connected" and "Offline" in the
/// person's language; the list's message count, rewritten by the script, stays in that language.
/// </summary>
public sealed partial class Rc19SteadyTextTests : IAsyncLifetime, IDisposable
{
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc19-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private WebApplication app = null!;
    private HttpClient client = null!;

    public void Dispose() => this.client?.Dispose();

    public async Task InitializeAsync()
    {
        var maildir = new MaildirStore(this.root, "test");
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "Imagiqa", PostmasterMailbox = "arun@anjal.co.in" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "arun", Domain = "anjal.co.in", DisplayName = "Arun Shiva", PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000) });
        this.app = Program.CreateApp(Array.Empty<string>(), this.store, maildir, "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
        string address = this.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        this.client = new HttpClient(handler) { BaseAddress = new Uri(address + "/") };
        string html = await (await this.client.GetAsync(new Uri("sign-in", UriKind.Relative))).Content.ReadAsStringAsync();
        Match m = TokenRegex.Match(html);
        Assert.True(m.Success, "no antiforgery token on sign-in");
        var form = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", m.Groups[1].Value),
            new("address", "arun@anjal.co.in"),
            new("password", "correct horse battery"),
        };
        using var content = new FormUrlEncodedContent(form);
        Assert.Equal(HttpStatusCode.Redirect, (await this.client.PostAsync(new Uri("auth/login", UriKind.Relative), content)).StatusCode);
    }

    public async Task DisposeAsync()
    {
        await this.app.StopAsync();
        await this.app.DisposeAsync();
        if (Directory.Exists(this.root))
        {
            TestFolders.Delete(this.root);
        }
    }

    [Fact]
    public async Task ConnectionLight_HoldsBothWordsSoItKeepsOneWidth()
    {
        string inbox = await this.client.GetStringAsync(new Uri("folder/INBOX", UriKind.Relative));
        MatchCollection lights = LightRegex().Matches(inbox);
        Assert.NotEmpty(lights);
        foreach (Match light in lights)
        {
            Assert.Equal("Connected", light.Groups["a"].Value);
            Assert.Equal("Offline", light.Groups["b"].Value);
            Assert.Equal("Connected", light.Groups["shown"].Value);
        }
        Assert.Equal(Regex.Count(inbox, "data-connectivity"), lights.Count);

        string css = await this.client.GetStringAsync(new Uri("app.css", UriKind.Relative));
        Assert.Contains(".slot::before { content: attr(data-slot-a); height: 0;", css, StringComparison.Ordinal);
        Assert.Contains(".slot::after { content: attr(data-slot-b); height: 0;", css, StringComparison.Ordinal);

        string script = await this.client.GetStringAsync(new Uri("app.js", UriKind.Relative));
        Assert.Contains("slot.setAttribute(\"data-slot-b\", off);", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessageCount_ScriptWritesItInThePersonsWords()
    {
        string inbox = await this.client.GetStringAsync(new Uri("folder/INBOX", UriKind.Relative));
        Assert.Matches("<span class=\"cnt sr-only\" data-count data-total=\"\\d+\" data-word=\"\\{n\\} messages\">", inbox);

        string script = await this.client.GetStringAsync(new Uri("app.js", UriKind.Relative));
        Assert.DoesNotContain("\" message\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\" messages\"", script, StringComparison.Ordinal);
    }

    [GeneratedRegex("<span class=\"lite\" data-connectivity [^>]*><i></i><span class=\"slot\" data-slot-a=\"(?<a>[^\"]*)\" data-slot-b=\"(?<b>[^\"]*)\">(?<shown>[^<]*)</span></span>")]
    private static partial Regex LightRegex();
}
