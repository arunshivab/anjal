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
/// rc.13 through the real pipeline: signing in with the user name alone,
/// "This computer is", the second step, signing out leaving nothing
/// behind, signing out other devices, the reset and invitation pages, and
/// the Mail and Security settings.
/// </summary>
public sealed class Rc13AuthFlowTests : IAsyncLifetime, IDisposable
{
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc13e-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly List<HttpClient> clients = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private string baseAddress = string.Empty;
    private MailboxRow mailbox = new();

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
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "Imagiqa" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        this.mailbox = await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.co.in",
            DisplayName = "Arun",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        });
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
    public async Task SignIn_WithTheUserNameAlone_OnTheOrganisationsPage()
    {
        HttpClient c = this.Client();
        string page = await c.GetStringAsync("sign-in");
        Assert.Contains("Imagiqa mail", page, StringComparison.Ordinal);
        Assert.Contains("@anjal.co.in</span>", page, StringComparison.Ordinal);
        Assert.Contains("This computer is", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/sign-in/forgot\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("No password reset here", page, StringComparison.Ordinal);

        HttpResponseMessage res = await SignInAsync(c, "Arun", "correct horse battery");
        Assert.Equal("/dashboard", res.Headers.Location!.ToString());
        // The person's own device: the cookie lasts (30 days), and it names a session.
        string cookie = res.Headers.GetValues("Set-Cookie").First(v => v.StartsWith("anjal.session=", StringComparison.Ordinal));
        Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("folder/INBOX")).StatusCode);
        Assert.Single(await this.app.Services.GetRequiredService<MailboxService>().GetSessionsAsync(this.mailbox.Id));
    }

    [Fact]
    public async Task SharedComputer_SessionCookieOnly_NoPageCached_AndSignOutClearsTheBrowser()
    {
        HttpClient c = this.Client();
        HttpResponseMessage res = await SignInAsync(c, "arun", "correct horse battery", ("device", "shared"));
        string cookie = res.Headers.GetValues("Set-Cookie").First(v => v.StartsWith("anjal.session=", StringComparison.Ordinal));
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        HttpResponseMessage inbox = await c.GetAsync("folder/INBOX");
        Assert.Contains("no-store", inbox.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        string html = await inbox.Content.ReadAsStringAsync();
        Assert.Contains("Shared computer", html, StringComparison.Ordinal);
        Assert.Contains("data-idle-limit=\"900\"", html, StringComparison.Ordinal);

        string token = Token(html);
        HttpResponseMessage outRes = await Post(c, "sign-out", token);
        Assert.StartsWith("/signed-out?at=", outRes.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal("\"cache\", \"storage\"", outRes.Headers.GetValues("Clear-Site-Data").Single());
        Assert.Empty(await this.app.Services.GetRequiredService<MailboxService>().GetSessionsAsync(this.mailbox.Id));
        string signedOut = await c.GetStringAsync(outRes.Headers.Location!.ToString().TrimStart('/'));
        Assert.Contains("You have signed out", signedOut, StringComparison.Ordinal);
        Assert.Contains("Signed out at", signedOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoStep_AsksForTheCode_ThenLetsIn_AndATrustedDeviceSkipsIt()
    {
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        (string _, string secretText) = (await svc.BeginAuthenticatorAsync(this.mailbox.Id))!.Value;
        byte[] secret = Base32.Decode(secretText)!;
        long step = Totp.StepOf(DateTimeOffset.UtcNow);
        Assert.Null((await svc.ConfirmAuthenticatorAsync(this.mailbox.Id, Totp.Code(secret, step - 1))).Error);

        HttpClient c = this.Client();
        HttpResponseMessage res = await SignInAsync(c, "arun", "correct horse battery");
        Assert.Equal("/sign-in/two-step", res.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("folder/INBOX")).StatusCode);   // not signed in yet
        string page = await c.GetStringAsync("sign-in/two-step");
        Assert.Contains("Enter the code", page, StringComparison.Ordinal);
        Assert.Equal(6, Regex.Count(page, "class=\"codebox\""));
        Assert.Contains("Trust this device for 30 days", page, StringComparison.Ordinal);

        string token = Token(page);
        HttpResponseMessage wrong = await Post(c, "auth/two-step", token, ("code", Totp.Code(secret, step - 1)));
        Assert.Equal("/sign-in/two-step?error=1", wrong.Headers.Location!.ToString());
        // Six boxes post six fields named "code"; the server reads the digits together.
        string code = Totp.Code(secret, step);
        var boxes = code.Select(ch => ("code", ch.ToString())).Append(("trust", "1")).ToArray();
        HttpResponseMessage right = await Post(c, "auth/two-step", token, boxes);
        Assert.Equal("/dashboard", right.Headers.Location!.ToString());
        Assert.Contains(right.Headers.GetValues("Set-Cookie"), v => v.StartsWith("anjal.trust=", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("folder/INBOX")).StatusCode);

        // Same browser, next time: the trusted device goes straight in...
        await Post(c, "sign-out", Token(await c.GetStringAsync("folder/INBOX")));
        Assert.Equal("/dashboard", (await SignInAsync(c, "arun", "correct horse battery")).Headers.Location!.ToString());
        // ...but never on a shared computer.
        await Post(c, "sign-out", Token(await c.GetStringAsync("folder/INBOX")));
        Assert.Equal("/sign-in/two-step", (await SignInAsync(c, "arun", "correct horse battery", ("device", "shared"))).Headers.Location!.ToString());
        string shared = await c.GetStringAsync("sign-in/two-step");
        Assert.Contains("Not available on a shared computer", shared, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignOutEverywhereElse_EndsTheOtherDevices_AtTheirNextRequest()
    {
        HttpClient laptop = this.Client();
        HttpClient phone = this.Client();
        await SignInAsync(laptop, "arun", "correct horse battery");
        await SignInAsync(phone, "arun", "correct horse battery");
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("folder/INBOX")).StatusCode);

        string security = await laptop.GetStringAsync("settings/security");
        Assert.Contains("Signed-in devices", security, StringComparison.Ordinal);
        Assert.Contains("This computer", security, StringComparison.Ordinal);
        Assert.Contains("Sign out everywhere else", security, StringComparison.Ordinal);
        HttpResponseMessage res = await Post(laptop, "settings/security/sessions/others", Token(security));
        Assert.Equal("/settings/security?saved=others", res.Headers.Location!.ToString());

        HttpResponseMessage after = await phone.GetAsync("folder/INBOX");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Equal("/sign-in?ended=1", after.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("folder/INBOX")).StatusCode);
    }

    [Fact]
    public async Task ChangingThePassword_SignsOutTheOtherDevices()
    {
        HttpClient laptop = this.Client();
        HttpClient phone = this.Client();
        await SignInAsync(laptop, "arun", "correct horse battery");
        await SignInAsync(phone, "arun", "correct horse battery");
        string token = Token(await laptop.GetStringAsync("settings/security"));
        HttpResponseMessage res = await Post(laptop, "settings/password", token, ("current", "correct horse battery"), ("next", "Quiet harbour mornings"));
        Assert.Equal("/settings/security?saved=password", res.Headers.Location!.ToString());
        Assert.Equal("/sign-in?ended=1", (await phone.GetAsync("folder/INBOX")).Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("folder/INBOX")).StatusCode);
    }

    [Fact]
    public async Task ForgotPassword_SendsACode_AndTheResetPageTakesIt()
    {
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = this.mailbox.TenantId, LocalPart = "home", Domain = "anjal.co.in" });
        Assert.Null(await svc.SetRecoveryAddressAsync(this.mailbox.Id, "correct horse battery", "home@anjal.co.in"));

        HttpClient c = this.Client();
        string forgot = await c.GetStringAsync("sign-in/forgot?u=arun");
        Assert.Contains("Forgot your password?", forgot, StringComparison.Ordinal);
        HttpResponseMessage sent = await Post(c, "auth/forgot", Token(forgot), ("user", "arun"));
        string reset = sent.Headers.Location!.ToString();
        Assert.StartsWith("/sign-in/reset?t=", reset, StringComparison.Ordinal);
        string page = await c.GetStringAsync(reset.TrimStart('/'));
        // DES-11 S1 (owner, 10 Oct 2026): the same words for every user name; the recovery address is not shown.
        Assert.Contains("If an account with that user name exists", page, StringComparison.Ordinal);
        Assert.DoesNotContain("h••••@anjal.co.in", WebUtility.HtmlDecode(page), StringComparison.Ordinal);
        Assert.Contains("data-pwrules", page, StringComparison.Ordinal);

        string body = this.store.MailboxMessages.Last(m => m.Subject.Contains("reset code", StringComparison.Ordinal)).BodyText;
        string code = Regex.Match(body, @"\b\d{6}\b").Value;
        string ticket = Uri.UnescapeDataString(Regex.Match(reset, "t=([^&]+)").Groups[1].Value);
        HttpResponseMessage done = await Post(c, "auth/reset", Token(page), ("t", ticket), ("code", code), ("password", "Brass-Lantern-77"));
        Assert.Equal("/sign-in?reset=1&u=arun", done.Headers.Location!.ToString());
        Assert.Equal("/dashboard", (await SignInAsync(this.Client(), "arun", "Brass-Lantern-77")).Headers.Location!.ToString());

        // A forged message cannot be put on the reset page: only sealed notes are shown.
        string forged = await c.GetStringAsync(reset.TrimStart('/') + "&e=Call%20this%20number");
        Assert.DoesNotContain("Call this number", forged, StringComparison.Ordinal);

        // A user name with no account gets the same page, and a code typed there is simply "not right".
        string nobody = (await Post(c, "auth/forgot", Token(forgot), ("user", "nobody"))).Headers.Location!.ToString();
        Assert.StartsWith("/sign-in/reset?t=", nobody, StringComparison.Ordinal);
        string decoyPage = await c.GetStringAsync(nobody.TrimStart('/'));
        Assert.Contains("If an account with that user name exists", decoyPage, StringComparison.Ordinal);
        Assert.Contains("data-pwrules", decoyPage, StringComparison.Ordinal);
        Assert.DoesNotContain("no account", decoyPage, StringComparison.OrdinalIgnoreCase);
        string decoyTicket = Uri.UnescapeDataString(Regex.Match(nobody, "t=([^&]+)").Groups[1].Value);
        string refused = (await Post(c, "auth/reset", Token(decoyPage), ("t", decoyTicket), ("code", "123456"), ("password", "Brass-Lantern-78"))).Headers.Location!.ToString();
        Assert.Contains("That code is not right", await c.GetStringAsync(refused.TrimStart('/')), StringComparison.Ordinal);
        Assert.Contains("sent=1", (await Post(c, "auth/reset/resend", Token(decoyPage), ("t", decoyTicket))).Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invitation_PageAndAcceptance_SignIn_AndLeadToTwoStep()
    {
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        MailboxRow meera = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = this.mailbox.TenantId, LocalPart = "meera.iyer", Domain = "anjal.co.in", DisplayName = "Meera Iyer" });
        (string token, DateTimeOffset _) = await svc.CreateInvitationAsync(meera.Id, "Arun Shiva B");

        HttpClient c = this.Client();
        string page = await c.GetStringAsync("invite/" + token);
        Assert.Contains("Welcome to Imagiqa mail, Meera", page, StringComparison.Ordinal);
        Assert.Contains("meera.iyer@anjal.co.in", page, StringComparison.Ordinal);
        Assert.Contains("This invitation from Arun Shiva B expires on", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Different from your last 5", page, StringComparison.Ordinal);

        HttpResponseMessage weak = await Post(c, "auth/invite", Token(page), ("token", token), ("password", "meera"), ("recovery", string.Empty));
        Assert.StartsWith("/invite/" + token + "?e=", weak.Headers.Location!.ToString(), StringComparison.Ordinal);
        HttpResponseMessage ok = await Post(c, "auth/invite", Token(page), ("token", token), ("password", "Quiet harbour mornings"), ("recovery", "meera@example.com"));
        Assert.Equal("/settings/security/two-step?welcome=1", ok.Headers.Location!.ToString());
        string step2 = await c.GetStringAsync("settings/security/two-step?welcome=1");
        Assert.Contains("Set up two-step sign-in", step2, StringComparison.Ordinal);
        Assert.Contains("<svg class=\"qr\"", step2, StringComparison.Ordinal);
        Assert.Contains("Skip for now", step2, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"welcome", step2, StringComparison.Ordinal);

        string used = await this.Client().GetStringAsync("invite/" + token);
        Assert.Contains("This invitation cannot be used", used, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PasswordCheck_TicksTheRules_WithoutTheHistory()
    {
        HttpClient c = this.Client();
        await SignInAsync(c, "arun", "correct horse battery");
        string token = Token(await c.GetStringAsync("settings/security"));
        string weak = await (await Post(c, "auth/password-check", token, ("password", "Arun@Arun-123456"))).Content.ReadAsStringAsync();
        Assert.Contains("\"length\":true", weak, StringComparison.Ordinal);
        Assert.Contains("\"personal\":false", weak, StringComparison.Ordinal);
        string leaked = await (await Post(c, "auth/password-check", token, ("password", "Sunshine@123"))).Content.ReadAsStringAsync();
        Assert.Contains("\"common\":false", leaked, StringComparison.Ordinal);
        string good = await (await Post(c, "auth/password-check", token, ("password", "Quiet harbour mornings"))).Content.ReadAsStringAsync();
        // Owner, 10 Oct 2026: no rule about kinds of characters; 15 characters without two-step.
        Assert.Equal("{\"length\":true,\"common\":true,\"personal\":true,\"least\":15}", good);
    }

    [Fact]
    public async Task MailSettings_UndoOff_SendsAtOnce()
    {
        HttpClient c = this.Client();
        await SignInAsync(c, "arun", "correct horse battery");
        string mail = await c.GetStringAsync("settings/mail");
        Assert.Contains("Undo send", mail, StringComparison.Ordinal);
        Assert.Contains("Empty Trash after", mail, StringComparison.Ordinal);
        Assert.Contains("What your organisation allows", mail, StringComparison.Ordinal);
        Assert.Equal("/settings/mail?saved=mail", (await Post(c, "settings/mail", Token(mail), ("undo", "0"))).Headers.Location!.ToString());
        Assert.Contains("/settings/mail?error=", (await Post(c, "settings/mail", Token(mail), ("trash", "200"))).Headers.Location!.ToString(), StringComparison.Ordinal);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(Token(await c.GetStringAsync("compose"))), "__RequestVerificationToken");
        form.Add(new StringContent("arun@anjal.co.in"), "to");
        form.Add(new StringContent("At once"), "subject");
        form.Add(new StringContent("No waiting"), "body");
        form.Add(new StringContent("1"), "undo");
        HttpResponseMessage sent = await c.PostAsync("compose", form);
        Assert.Equal("/folder/Sent?sent=1", sent.Headers.Location!.ToString());
    }

    [Fact]
    public async Task AuthPages_StandAlone_EvenWhenSignedIn()
    {
        HttpClient c = this.Client();
        await SignInAsync(c, "arun", "correct horse battery");
        string page = await c.GetStringAsync("invite/nothing-here");
        Assert.Contains("This invitation cannot be used", page, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"rail", page, StringComparison.Ordinal);
        Assert.Contains("You have signed out", await c.GetStringAsync("signed-out"), StringComparison.Ordinal);
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

    private HttpClient Client()
    {
        var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(this.baseAddress + "/") };
        this.clients.Add(c);
        return c;
    }

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient c, string user, string password, params (string Key, string Value)[] extra)
    {
        string token = Token(await c.GetStringAsync("sign-in"));
        HttpResponseMessage res = await Post(c, "auth/login", token, new[] { ("address", user), ("password", password) }.Concat(extra).ToArray());
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        return res;
    }
}
