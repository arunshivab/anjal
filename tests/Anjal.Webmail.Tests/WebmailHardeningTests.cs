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

public sealed class WebmailHardeningTests : IAsyncLifetime, System.IDisposable
{
    public void Dispose() => this.client?.Dispose();

    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly string[] ArunRecipient = new[] { "arun@anjal.co.in" };

    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-webmail-hard-" + System.Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private HttpClient client = null!;
    private MailboxRow mailbox = new();

    public async System.Threading.Tasks.Task InitializeAsync()
    {
        this.maildir = new MaildirStore(this.root, "test");
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        // A hash with deliberately few rounds, as an account created before
        // the iteration count was raised would have.
        this.mailbox = await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.co.in",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        });
        this.app = Program.CreateApp(System.Array.Empty<string>(), this.store, this.maildir, "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
        string address = this.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        this.client = new HttpClient(handler) { BaseAddress = new System.Uri(address.Replace("127.0.0.1:0", "127.0.0.1", System.StringComparison.Ordinal) + "/") };
    }

    public async System.Threading.Tasks.Task DisposeAsync()
    {
        this.client.Dispose();
        await this.app.StopAsync();
        await this.app.DisposeAsync();
        if (System.IO.Directory.Exists(this.root))
        {
            System.IO.Directory.Delete(this.root, recursive: true);
        }
    }

    private async System.Threading.Tasks.Task<string> TokenAsync(string path)
    {
        string html = await (await this.client.GetAsync(path)).Content.ReadAsStringAsync();
        Match m = TokenRegex.Match(html);
        Assert.True(m.Success, "no antiforgery token on " + path);
        return m.Groups[1].Value;
    }

    private async System.Threading.Tasks.Task<HttpResponseMessage> PostAsync(string path, string token, params (string Key, string Value)[] fields)
    {
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        foreach ((string k, string v) in fields)
        {
            form.Add(new KeyValuePair<string, string>(k, v));
        }
        return await this.client.PostAsync(path, new FormUrlEncodedContent(form));
    }

    private async System.Threading.Tasks.Task<HttpResponseMessage> SignInAsync(string password)
    {
        string token = await this.TokenAsync("sign-in");
        return await this.PostAsync("auth/login", token, ("address", "arun@anjal.co.in"), ("password", password));
    }

    private async System.Threading.Tasks.Task<MessageRow> DeliverAsync(string raw)
    {
        await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "s@x.test",
            EnvelopeTo = ArunRecipient,
            RawBytes = System.Text.Encoding.UTF8.GetBytes(raw),
        });
        return this.store.MailboxMessages[this.store.MailboxMessages.Count - 1];
    }

    [Fact]
    public async System.Threading.Tasks.Task EveryResponse_CarriesTheSecurityHeaders()
    {
        HttpResponseMessage res = await this.client.GetAsync("sign-in");
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", res.Headers.GetValues("Referrer-Policy").Single());
        string csp = res.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self'", csp, System.StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, System.StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", csp, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task NoPage_UsesAnInlineEventHandler_WhichTheCspWouldBlock()
    {
        Assert.Equal(HttpStatusCode.Redirect, (await this.SignInAsync("correct horse battery")).StatusCode);
        foreach (string path in new[] { "folder/INBOX", "dashboard", "settings", "compose", "search?q=a" })
        {
            string html = await (await this.client.GetAsync(path)).Content.ReadAsStringAsync();
            Assert.DoesNotMatch(new Regex("\\son[a-z]+=\"", RegexOptions.CultureInvariant), html);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task RepeatedFailedSignIns_AreThrottled_AndAudited()
    {
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal("/sign-in?error=1", (await this.SignInAsync("wrong")).Headers.Location!.ToString());
        }
        // Even the right password is refused while throttled.
        Assert.Equal("/sign-in?throttled=1", (await this.SignInAsync("correct horse battery")).Headers.Location!.ToString());

        IReadOnlyList<AuditEvent> events = await this.store.ListAuditAsync(20);
        Assert.Contains(events, e => e.Action == "webmail.signin.failed");
        Assert.Contains(events, e => e.Action == "webmail.signin.throttled");
    }

    [Fact]
    public async System.Threading.Tasks.Task ASuccessfulSignIn_UpgradesAnOldHash_AndIsAudited()
    {
        Assert.True(Pbkdf2Hasher.NeedsRehash(this.mailbox.PasswordPbkdf2));
        Assert.Equal("/folder/INBOX", (await this.SignInAsync("correct horse battery")).Headers.Location!.ToString());
        string upgraded = (await this.store.GetMailboxByIdAsync(this.mailbox.Id))!.PasswordPbkdf2;
        Assert.False(Pbkdf2Hasher.NeedsRehash(upgraded));
        Assert.True(Pbkdf2Hasher.Verify("correct horse battery", upgraded));
        Assert.Contains(await this.store.ListAuditAsync(10), e => e.Action == "webmail.signin" && e.Actor == "arun@anjal.co.in");
    }

    [Fact]
    public async System.Threading.Tasks.Task BackLinks_ThatLeaveTheSite_AreReplacedWithTheFallback()
    {
        await this.SignInAsync("correct horse battery");
        MessageRow m = await this.DeliverAsync("Subject: s\r\n\r\nbody\r\n");
        foreach (string evil in new[] { "//evil.example/", "/\\evil.example/", "https://evil.example/", "/ok\\..\\x" })
        {
            string token = await this.TokenAsync("folder/INBOX");
            HttpResponseMessage res = await this.PostAsync($"message/{m.Id}/flag", token, ("back", evil));
            Assert.Equal($"/message/{m.Id}", res.Headers.Location!.ToString());
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task MovingToAnArbitraryFolderName_IsRefused_AndCreatesNothing()
    {
        await this.SignInAsync("correct horse battery");
        MessageRow m = await this.DeliverAsync("Subject: s\r\n\r\nbody\r\n");
        foreach (string target in new[] { "../x", "Invented", "" })
        {
            string token = await this.TokenAsync("folder/INBOX");
            HttpResponseMessage res = await this.PostAsync($"message/{m.Id}/move", token, ("folder", target), ("back", "/folder/INBOX"));
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
        Assert.DoesNotContain(await this.store.ListFoldersAsync(this.mailbox.Id), f => f.Name == "Invented");
    }

    [Fact]
    public async System.Threading.Tasks.Task OversizedAttachments_AreRefusedWithAMessage_BeforeBeingRead()
    {
        await this.SignInAsync("correct horse battery");
        string token = await this.TokenAsync("compose");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent("someone@example.com"), "to" },
            { new StringContent("big"), "subject" },
            { new ByteArrayContent(new byte[19 * 1024 * 1024]), "attachments", "big.bin" },
        };
        HttpResponseMessage res = await this.client.PostAsync("compose", form);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("18%20MB", res.Headers.Location!.ToString(), System.StringComparison.Ordinal);
        Assert.Empty(await this.store.LeaseOutboundBatchAsync(10, System.DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    [Fact]
    public async System.Threading.Tasks.Task TheMessageFrame_CarriesItsOwnPolicy()
    {
        await this.SignInAsync("correct horse battery");
        MessageRow m = await this.DeliverAsync("Subject: s\r\nContent-Type: text/html\r\n\r\n<p>hi</p>\r\n");
        string html = WebUtility.HtmlDecode(await (await this.client.GetAsync($"message/{m.Id}")).Content.ReadAsStringAsync());
        Assert.Contains("default-src 'none'", html, System.StringComparison.Ordinal);
        Assert.DoesNotContain("img-src data: https:", html, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task ADisplayNameWithALineBreak_IsRefused()
    {
        await this.SignInAsync("correct horse battery");
        string token = await this.TokenAsync("settings");
        HttpResponseMessage res = await this.PostAsync("settings/name", token, ("displayName", "Arun\r\nBcc: everyone@x.test"));
        Assert.Contains("error=", res.Headers.Location!.ToString(), System.StringComparison.Ordinal);
        Assert.Equal(string.Empty, (await this.store.GetMailboxByIdAsync(this.mailbox.Id))!.DisplayName);
    }

    [Fact]
    public async System.Threading.Tasks.Task AForgedReplyLink_CannotInjectHeaders()
    {
        await this.SignInAsync("correct horse battery");
        string token = await this.TokenAsync("compose");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent("someone@example.com"), "to" },
            { new StringContent("hi"), "subject" },
            { new StringContent("text"), "body" },
            { new StringContent("abc\r\nFrom: ceo@othertenant.test\r\nX-Injected: yes"), "inReplyTo" },
        };
        HttpResponseMessage res = await this.client.PostAsync("compose", form);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        OutboundMessage sent = Assert.Single(await this.store.LeaseOutboundBatchAsync(10, System.DateTimeOffset.UtcNow.AddMinutes(1)));
        string raw = System.Text.Encoding.ASCII.GetString(sent.RawBytes);
        Assert.DoesNotContain("X-Injected", raw, System.StringComparison.Ordinal);
        Assert.DoesNotContain("ceo@othertenant", raw, System.StringComparison.Ordinal);
        Assert.DoesNotContain("In-Reply-To", raw, System.StringComparison.Ordinal);
    }
}

public class SanitizerHardeningTests
{
    [Theory]
    [InlineData("<script/src=//evil.example>alert(1)</script>")]
    [InlineData("<img/src=x/onerror=alert(1)>")]
    [InlineData("<svg/onload=alert(1)>")]
    [InlineData("<scr<script>ipt>alert(1)</script>")]
    [InlineData("<a href=\"java\tscript:alert(1)\">x</a>")]
    public void KnownBypasses_ProduceNoActiveMarkup(string html)
    {
        string clean = HtmlSanitizer.Sanitize(html);
        Assert.DoesNotContain("<script", clean, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<svg", clean, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", clean, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onload", clean, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript", clean.Replace("\t", string.Empty, System.StringComparison.Ordinal), System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("background:\\75rl(http://t/p)")]
    [InlineData("background:url(http://t/p)")]
    [InlineData("background-image: image-set('x.png' 1x)")]
    [InlineData("color:red;/**/background:url(x)")]
    [InlineData("behavior:url(x.htc)")]
    [InlineData("width:expression(alert(1))")]
    [InlineData("@import 'x.css'")]
    public void StylesThatCouldFetchOrEscape_AreDropped(string style)
    {
        Assert.DoesNotContain("url", HtmlSanitizer.SanitizeStyle(style), System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expression", HtmlSanitizer.SanitizeStyle(style), System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("image-set", HtmlSanitizer.SanitizeStyle(style), System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OrdinaryStyles_Survive()
    {
        Assert.Equal("color:#333;font-weight:bold", HtmlSanitizer.SanitizeStyle("color:#333; font-weight:bold; position:fixed"));
        Assert.Equal("background-color:rgb(1, 2, 3)", HtmlSanitizer.SanitizeStyle("background-color:rgb(1, 2, 3)"));
    }

    [Fact]
    public void StrayAngleBrackets_InText_AreEscaped()
    {
        Assert.Equal("a &lt; b &gt; c", HtmlSanitizer.Sanitize("a < b > c"));
    }

    [Fact]
    public void RedirectHosts_AreLimitedToThisServersNames()
    {
        Assert.True(Program.IsOwnHost("mail.anjal.co.in", "mail.anjal.co.in", null));
        Assert.True(Program.IsOwnHost("MAIL.ANJAL.CO.IN", "mail.anjal.co.in", null));
        Assert.True(Program.IsOwnHost("127.0.0.1", "mail.anjal.co.in", null));
        Assert.False(Program.IsOwnHost("evil.example", "mail.anjal.co.in", null));
        Assert.False(Program.IsOwnHost(string.Empty, "mail.anjal.co.in", null));
    }

    [Fact]
    public void LoginThrottle_KeysOnAddressAndAccount_AndOnTheAccountAlone()
    {
        var now = new System.DateTimeOffset(2026, 9, 21, 10, 0, 0, System.TimeSpan.Zero);
        var t = new LoginThrottle(2, 3, System.TimeSpan.FromMinutes(15), () => now);
        t.RecordFailure("10.0.0.1", "a@x.test");
        t.RecordFailure("10.0.0.1", "a@x.test");
        Assert.False(t.IsAllowed("10.0.0.1", "a@x.test"));
        Assert.True(t.IsAllowed("10.0.0.1", "b@x.test"));   // a colleague behind the same NAT
        Assert.True(t.IsAllowed("10.0.0.2", "a@x.test"));
        t.RecordFailure("10.0.0.2", "a@x.test");             // third failure on the account
        Assert.False(t.IsAllowed("10.0.0.3", "a@x.test"));   // distributed guessing capped
    }

    [Fact]
    public void AMiss_CostsAsMuchAsACheck_AndIsAlwaysFalse()
    {
        Assert.False(Pbkdf2Hasher.VerifyAgainstDummy("anything"));
        Assert.Contains("$600000$", Pbkdf2Hasher.Hash("x"), System.StringComparison.Ordinal);
    }

    [Fact]
    public void MessageIds_AreValidatedBeforeUse()
    {
        Assert.True(MailboxService.IsValidMessageId("abc.123@apulki.in"));
        Assert.False(MailboxService.IsValidMessageId("abc\r\nFrom: x@y"));
        Assert.False(MailboxService.IsValidMessageId("no-at-sign"));
        Assert.False(MailboxService.IsValidMessageId("two@at@signs"));
        Assert.False(MailboxService.IsValidMessageId("has space@x"));
        Assert.False(MailboxService.IsValidMessageId(new string('a', 251) + "@x"));
    }
}
