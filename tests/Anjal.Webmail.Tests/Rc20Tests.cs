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
/// rc.20 (owner, 14 Oct 2026, after using rc.19): a size on screen for each person (Windows at 125%
/// made everything look a quarter too big); one frame for every help page and Lajna's page, with a
/// small search on each, no release label, and a way back from Lajna's page; Lajna understands
/// "Priya's last 5 mails" and part of an address, and still writes a draft to someone it cannot
/// find; "Sign in again" is as wide as "Sign in".
/// </summary>
public sealed partial class Rc20Tests : IAsyncLifetime, IDisposable
{
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly string[] ToArun = { "arun@anjal.co.in" };
    private static readonly string[] SharedPages = { "help", "help/part/security", "help/two-step", "help?q=two-step", "lajna?page=/folder/INBOX" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc20-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private HttpClient client = null!;
    private MailboxRow arun = new();

    public void Dispose() => this.client?.Dispose();

    public async Task InitializeAsync()
    {
        this.maildir = new MaildirStore(this.root, "test");
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "Imagiqa", PostmasterMailbox = "arun@anjal.co.in" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        this.arun = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "arun", Domain = "anjal.co.in", DisplayName = "Arun Shiva", PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000) });
        await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "meera", Domain = "anjal.co.in", DisplayName = "Meera Iyer", PasswordPbkdf2 = Pbkdf2Hasher.Hash("another long password", 1000) });
        this.app = Program.CreateApp(Array.Empty<string>(), this.store, this.maildir, "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
        string address = this.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        this.client = new HttpClient(handler) { BaseAddress = new Uri(address + "/") };
        string token = await this.TokenAsync("sign-in");
        Assert.Equal(HttpStatusCode.Redirect, (await this.PostAsync("auth/login", token, ("address", "arun@anjal.co.in"), ("password", "correct horse battery"))).StatusCode);
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
    public async Task SizeOnScreen_IsSavedForThePerson_AndWrittenOnEveryPage()
    {
        Assert.Contains("data-zoom=\"100\"", await this.client.GetStringAsync(new Uri("folder/INBOX", UriKind.Relative)), StringComparison.Ordinal);
        string token = await this.TokenAsync("settings/appearance");
        HttpResponseMessage saved = await this.PostAsync("settings/appearance", token, ("colour", "anjal"), ("mode", "light"), ("zoom", "80"));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal(80, (await this.store.GetMailboxByIdAsync(this.arun.Id))!.Zoom);
        string page = await this.client.GetStringAsync(new Uri("settings/appearance", UriKind.Relative));
        Assert.Contains("data-zoom=\"80\"", page, StringComparison.Ordinal);
        Assert.Matches("name=\"zoom\" value=\"80\" checked", page);

        token = await this.TokenAsync("folder/INBOX");
        await this.PostAsync("settings/zoom", token, ("zoom", "55"), ("back", "/folder/INBOX"));
        Assert.Equal(MailboxPreferences.DefaultZoom, (await this.store.GetMailboxByIdAsync(this.arun.Id))!.Zoom);
        Assert.Contains("data-zoom=\"100\"", await this.client.GetStringAsync(new Uri("folder/INBOX", UriKind.Relative)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SizeOnScreen_KeepsTheFrameFillingTheWindow()
    {
        string css = await this.client.GetStringAsync(new Uri("app.css", UriKind.Relative));
        Assert.Contains("html[data-zoom=\"80\"] { --z: 0.8; zoom: 0.8; }", css, StringComparison.Ordinal);
        Assert.Contains("html[data-zoom=\"90\"] { --z: 0.9; zoom: 0.9; }", css, StringComparison.Ordinal);
        // Every length measured from the window is divided by the zoom, so a zoomed page still fills it.
        MatchCollection units = ViewportUnit().Matches(css);
        Assert.NotEmpty(units);
        Assert.All(units, m => Assert.EndsWith(" / var(--z, 1))", css.Substring(m.Index, Math.Min(css.Length - m.Index, m.Length + 15)), StringComparison.Ordinal));
        string script = await this.client.GetStringAsync(new Uri("app.js", UriKind.Relative));
        Assert.Contains("var zoomOf = function ()", script, StringComparison.Ordinal);
        Assert.Contains("tip.style.left = (left / z) + \"px\";", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lajna_OffersASmallerSize_OnlyWithYes()
    {
        string page = await this.client.GetStringAsync(new Uri("lajna/answer?q=" + Uri.EscapeDataString("make everything smaller") + "&page=/folder/INBOX", UriKind.Relative));
        Assert.Contains("class=\"ljconsent\"", page, StringComparison.Ordinal);
        Assert.Contains("action=\"/settings/zoom\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"zoom\" value=\"90\"", page, StringComparison.Ordinal);
        Assert.Equal(MailboxPreferences.DefaultZoom, (await this.store.GetMailboxByIdAsync(this.arun.Id))!.Zoom);
    }

    [Fact]
    public async Task EveryHelpPage_AndLajnasPage_ShareOneFrame_WithTheSearchOnTop()
    {
        foreach (string path in SharedPages)
        {
            string html = await this.client.GetStringAsync(new Uri(path, UriKind.Relative));
            Assert.True(html.Contains("class=\"helpshell\"", StringComparison.Ordinal), path);
            Assert.True(html.Contains("class=\"helpbar\"", StringComparison.Ordinal), path);
            Assert.True(html.Contains("class=\"helpnav\"", StringComparison.Ordinal), path);
            Assert.True(html.Contains("class=\"helpside\"", StringComparison.Ordinal), path);
        }
        string article = await this.client.GetStringAsync(new Uri("help/two-step", UriKind.Relative));
        Assert.DoesNotMatch("Anjal rc\\.\\d", article);
        Assert.Contains("min read · In English", article, StringComparison.Ordinal);
        string part = await this.client.GetStringAsync(new Uri("help/part/security", UriKind.Relative));
        Assert.Contains("href=\"/help/two-step\"", part, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LajnasPage_HasAWayBack_AndOpensAsAPanel()
    {
        string page = await this.client.GetStringAsync(new Uri("lajna?page=" + Uri.EscapeDataString("/settings/appearance"), UriKind.Relative));
        Assert.Matches("<a class=\"ljback\" href=\"/settings/appearance\" data-lajna-back", page);
        Assert.Contains("href=\"/settings/appearance#lajna-open\"", page, StringComparison.Ordinal);
        string script = await this.client.GetStringAsync(new Uri("app.js", UriKind.Relative));
        Assert.Contains("window.location.hash !== \"#lajna-open\"", script, StringComparison.Ordinal);
        Assert.Contains("var back = $(\"[data-lajna-back]\");", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lajna_FindsMail_AskedForInMoreWays()
    {
        await this.DeliverAsync(ToArun, "From: Arun Shiva <arunshiva@redifmail.test>\r\nSubject: Third day test mail\r\n\r\nAnother test.\r\n");
        await this.DeliverAsync(ToArun, "From: Meera Iyer <meera@anjal.co.in>\r\nSubject: Duty roster\r\n\r\nSee the roster.\r\n");
        foreach (string q in new[] { "arunshiva@redifmail's last 5 mails", "find arunshiva@redifmail's last 5 mails" })
        {
            string found = await this.client.GetStringAsync(new Uri("lajna/answer?q=" + Uri.EscapeDataString(q) + "&page=/", UriKind.Relative));
            Assert.True(found.Contains("class=\"ljmails\"", StringComparison.Ordinal), q);
            Assert.True(found.Contains("Third day test mail", StringComparison.Ordinal), q);
        }
        foreach (string q in new[] { "Meera's last 2 mails", "last 5 mails of Meera", "show me Meera’s mails" })
        {
            string found = await this.client.GetStringAsync(new Uri("lajna/answer?q=" + Uri.EscapeDataString(q) + "&page=/", UriKind.Relative));
            Assert.True(found.Contains("Duty roster", StringComparison.Ordinal), q);
        }
    }

    [Fact]
    public async Task Lajna_WritesTheDraft_EvenWhenItCannotFindThePerson()
    {
        string page = WebUtility.HtmlDecode(await this.client.GetStringAsync(new Uri("lajna/answer?q=" + Uri.EscapeDataString("write a mail to HR asking for leave on Friday") + "&page=/", UriKind.Relative)));
        Assert.Contains("class=\"ljdraft\"", page, StringComparison.Ordinal);
        Assert.Contains("I do not know HR's address, so the To line is empty.", page, StringComparison.Ordinal);
        Assert.Contains("(add the address in Compose)", page, StringComparison.Ordinal);
        Assert.Contains("Dear HR team,", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/compose?to=&subject=Leave", page, StringComparison.Ordinal);
    }

    [Fact]
    public void ALeaveLetter_KeepsTheDay()
    {
        // Owner's screen, 14 Oct 2026: "asking for leave on Friday" came back as "Leave request".
        LajnaDraft friday = LajnaLetters.Write(string.Empty, "HR", "asking for leave on Friday", true, null, "Arun", string.Empty, "normal");
        Assert.Equal("Leave request: Friday", friday.Subject);
        Assert.Contains("I would like to request leave on Friday.", friday.Body, StringComparison.Ordinal);
        Assert.Equal("Leave request: tomorrow", LajnaLetters.Write("hr@anjal.co.in", "HR", "for leave tomorrow", true, null, "Arun", string.Empty, "normal").Subject);
        Assert.Contains("leave on 14 Oct", LajnaLetters.Write("hr@anjal.co.in", "HR", "for leave on 14 Oct", true, null, "Arun", string.Empty, "short").Body, StringComparison.Ordinal);
        Assert.Contains("leave from 6 to 10 Nov", LajnaLetters.Write("hr@anjal.co.in", "HR", "for leave 6-10 Nov", true, null, "Arun", string.Empty, "normal").Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignInAgain_IsAsWideAsSignIn()
    {
        string css = await this.client.GetStringAsync(new Uri("app.css", UriKind.Relative));
        Match rule = Regex.Match(css, @"\.authbtn \{[^}]*\}");
        Assert.True(rule.Success);
        Assert.Contains("width: 100%; padding: 0 20px; box-sizing: border-box;", rule.Value, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"(?<![\w.-])\d+(?:\.\d+)?(?:dvh|vh|vw)\b")]
    private static partial Regex ViewportUnit();

    private async Task DeliverAsync(string[] to, string raw)
    {
        await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "s@x.test",
            EnvelopeTo = to,
            RawBytes = System.Text.Encoding.UTF8.GetBytes(raw),
        });
    }

    private async Task<string> TokenAsync(string path)
    {
        string html = await (await this.client.GetAsync(new Uri(path, UriKind.Relative))).Content.ReadAsStringAsync();
        Match m = TokenRegex.Match(html);
        Assert.True(m.Success, "no antiforgery token on " + path);
        return m.Groups[1].Value;
    }

    private async Task<HttpResponseMessage> PostAsync(string path, string token, params (string Key, string Value)[] fields)
    {
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        foreach ((string k, string v) in fields)
        {
            form.Add(new KeyValuePair<string, string>(k, v));
        }
        using var content = new FormUrlEncodedContent(form);
        return await this.client.PostAsync(new Uri(path, UriKind.Relative), content);
    }
}
