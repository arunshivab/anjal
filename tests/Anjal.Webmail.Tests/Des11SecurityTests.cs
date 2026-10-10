using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
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
/// The security points of ANJAL-DES-11, as the owner decided them on 10 Oct 2026: S2 a new
/// password forgets trusted devices and the person is told; S3 five wrong second-step codes lock
/// it for fifteen minutes; S4 security changes ask to confirm it is you; S5 sign-in alerts with
/// "This wasn't me"; S6 DKIM keys locked with the mail server's seal key. (S1 is held in
/// <see cref="Rc13AuthFlowTests"/>.)
/// </summary>
public sealed class Des11SecurityTests : IAsyncLifetime, IDisposable
{
    private const string Password = "correct horse battery";
    private const string Laptop = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Safari/537.36";
    private const string Phone = "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Mobile Safari/537.36";
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-des11s-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly List<HttpClient> clients = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private string baseAddress = string.Empty;
    private TenantRow tenant = new();
    private MailboxRow arun = new();

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
        this.tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "Imagiqa" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.tenant.Id, Domain = "anjal.co.in" });
        this.arun = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = this.tenant.Id, LocalPart = "arun", Domain = "anjal.co.in", DisplayName = "Arun", PasswordPbkdf2 = Pbkdf2Hasher.Hash(Password, 1000) });
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

    /// <summary>S2: a new password forgets every trusted device, and the person is mailed.</summary>
    [Fact]
    public async Task S2_ANewPassword_ForgetsTrustedDevices_AndTellsThePerson()
    {
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        await svc.TrustDeviceAsync(this.arun.Id, "Windows, Chrome");
        Assert.NotEmpty((await svc.GetSecurityAsync(this.arun.Id)).TrustedDevices);

        HttpClient c = await this.SignedInAsync(Laptop);
        string page = await c.GetStringAsync(new Uri("settings/security", UriKind.Relative));
        using HttpResponseMessage res = await Post(c, "settings/password", Token(page), ("current", Password), ("next", "Quiet harbour mornings"));
        Assert.Equal("/settings/security?saved=password", res.Headers.Location!.ToString());
        Assert.Empty((await svc.GetSecurityAsync(this.arun.Id)).TrustedDevices);
        Assert.Contains("every trusted device will ask for the second step once more", await c.GetStringAsync(new Uri("settings/security?saved=password", UriKind.Relative)), StringComparison.Ordinal);
        Assert.Contains(this.Subjects(), s => s == "Your password was changed");
    }

    /// <summary>S3: five wrong codes lock the second step for fifteen minutes on every device; the person is mailed.</summary>
    [Fact]
    public async Task S3_FiveWrongCodes_LockTheSecondStep_OnEveryDevice()
    {
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        (string _, string secretText) = (await svc.BeginAuthenticatorAsync(this.arun.Id))!.Value;
        byte[] secret = Base32.Decode(secretText)!;
        Assert.Null((await svc.ConfirmAuthenticatorAsync(this.arun.Id, Totp.Code(secret, Totp.StepOf(DateTimeOffset.UtcNow) - 1))).Error);

        HttpClient c = this.Client(Laptop);
        Assert.Equal("/sign-in/two-step", (await SignInAsync(c)).Headers.Location!.ToString());
        string token = Token(await c.GetStringAsync(new Uri("sign-in/two-step", UriKind.Relative)));
        string last = string.Empty;
        for (int i = 0; i < 5; i++)
        {
            using HttpResponseMessage wrong = await Post(c, "auth/two-step", token, ("code", "000000"));
            last = wrong.Headers.Location!.ToString();
        }
        Assert.Equal("/sign-in?locked=1", last);
        Assert.Contains("locked for fifteen minutes", await c.GetStringAsync(new Uri("sign-in?locked=1", UriKind.Relative)), StringComparison.Ordinal);
        Assert.Contains(this.Subjects(), s => s == "Your sign-in was locked for fifteen minutes");

        // Another device, with the right password: still locked.
        HttpClient other = this.Client(Phone);
        Assert.StartsWith("/sign-in?locked=1", (await SignInAsync(other)).Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    /// <summary>S4: a security change asks to confirm it is you when the password is more than five minutes old.</summary>
    [Fact]
    public async Task S4_ASecurityChange_AsksToConfirm_AfterFiveMinutes()
    {
        HttpClient c = await this.SignedInAsync(Laptop);
        string page = await c.GetStringAsync(new Uri("settings/security", UriKind.Relative));
        using (HttpResponseMessage fresh = await Post(c, "settings/security/backup-codes", Token(page)))
        {
            Assert.Equal("/settings/security/two-step?codes=1", fresh.Headers.Location!.ToString());
        }

        this.ForgetProofs();
        using HttpResponseMessage stale = await Post(c, "settings/security/backup-codes", Token(page));
        Assert.StartsWith("/settings/security/confirm?next=", stale.Headers.Location!.ToString(), StringComparison.Ordinal);
        string confirm = await c.GetStringAsync(new Uri(stale.Headers.Location!.ToString().TrimStart('/'), UriKind.Relative));
        Assert.Contains("Confirm it is you", confirm, StringComparison.Ordinal);
        using (HttpResponseMessage wrong = await Post(c, "settings/security/confirm", Token(confirm), ("password", "not it"), ("next", "/settings/security")))
        {
            Assert.Contains("wrong=1", wrong.Headers.Location!.ToString(), StringComparison.Ordinal);
        }
        using (HttpResponseMessage ok = await Post(c, "settings/security/confirm", Token(confirm), ("password", Password), ("next", "/settings/security")))
        {
            Assert.Equal("/settings/security", ok.Headers.Location!.ToString());
        }
        using HttpResponseMessage now = await Post(c, "settings/security/backup-codes", Token(page));
        Assert.Equal("/settings/security/two-step?codes=1", now.Headers.Location!.ToString());
        Assert.Contains(this.Subjects(), s => s == "New backup codes were made");
    }

    /// <summary>
    /// S5: the first sign-in is quiet; a sign-in from a device not seen before mails the person with
    /// "This was me" and "This wasn't me"; the second link signs that device out at once; the status
    /// bar shows the sign-in before this one.
    /// </summary>
    [Fact]
    public async Task S5_ANewDevice_IsAlerted_AndThisWasntMe_SignsItOut()
    {
        HttpClient laptop = await this.SignedInAsync(Laptop);
        Assert.DoesNotContain(this.Subjects(), s => s.StartsWith("New sign-in", StringComparison.Ordinal));

        HttpClient phone = await this.SignedInAsync(Phone);
        MessageRow alert = this.store.MailboxMessages.Last(m => m.MailboxId == this.arun.Id && m.Subject.StartsWith("New sign-in", StringComparison.Ordinal));
        Assert.Contains("Android", alert.BodyText, StringComparison.Ordinal);
        Assert.Contains("This was me: ", alert.BodyText, StringComparison.Ordinal);
        string notMe = Regex.Match(alert.BodyText, @"/sign-in/check\?a=not-me&t=(\S+)").Groups[1].Value;
        Assert.NotEmpty(notMe);
        Assert.Contains("Last sign-in: ", await phone.GetStringAsync(new Uri("folder/INBOX", UriKind.Relative)), StringComparison.Ordinal);

        HttpClient stranger = this.Client(Laptop);
        string check = await stranger.GetStringAsync(new Uri("sign-in/check?a=not-me&t=" + notMe, UriKind.Relative));
        Assert.Contains("It wasn&#x27;t you?", check, StringComparison.Ordinal);
        using HttpResponseMessage done = await Post(stranger, "auth/not-me", Token(check), ("t", Uri.UnescapeDataString(notMe)));
        Assert.Equal("/sign-in/check?done=1", done.Headers.Location!.ToString());
        Assert.Equal("/sign-in?ended=1", (await phone.GetAsync(new Uri("folder/INBOX", UriKind.Relative))).Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync(new Uri("folder/INBOX", UriKind.Relative))).StatusCode);
    }

    /// <summary>
    /// S6: with the mail server's seal published, the webmail makes a domain's DKIM key locked with
    /// it - the stored value never holds the private key in the clear, the DNS record is still shown,
    /// and only the seal's private half opens it.
    /// </summary>
    [Fact]
    public async Task S6_TheWebmail_LocksEachDkimKey_WithTheMailServersSeal()
    {
        using RSA seal = RSA.Create(3072);
        await this.store.SetServiceRecordAsync(KeySeal.PublicRecordKind, System.Text.Json.JsonSerializer.Serialize(new SealRecord { PublicKey = Convert.ToBase64String(seal.ExportSubjectPublicKeyInfo()), Made = DateTimeOffset.UtcNow }));
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        Assert.True(await svc.CanMakeDkimKeysAsync());

        Assert.Null(await svc.AddTenantDomainAsync(this.tenant, "clinic.example"));
        DkimKeyRow key = (await this.store.GetDkimKeyAsync("clinic.example"))!;
        Assert.True(KeySeal.IsSealed(key.PrivateKeyPem));
        Assert.DoesNotContain("PRIVATE KEY", key.PrivateKeyPem, StringComparison.Ordinal);

        string pem = KeySeal.Open(key.PrivateKeyPem, seal, "clinic.example", key.Selector);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        Assert.Equal(KeySeal.DkimPublicKey(key.PrivateKeyPem), rsa.ExportSubjectPublicKeyInfo());
        Assert.ThrowsAny<CryptographicException>(() => KeySeal.Open(key.PrivateKeyPem, seal, "other.example", key.Selector));
        using RSA wrong = RSA.Create(3072);
        Assert.ThrowsAny<CryptographicException>(() => KeySeal.Open(key.PrivateKeyPem, wrong, "clinic.example", key.Selector));
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
        return PostFormAsync(c, path, form);
    }

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient c, string path, List<KeyValuePair<string, string>> form)
    {
        using var content = new FormUrlEncodedContent(form);
        return await c.PostAsync(new Uri(path, UriKind.Relative), content).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient c)
    {
        string token = Token(await c.GetStringAsync(new Uri("sign-in", UriKind.Relative)).ConfigureAwait(false));
        HttpResponseMessage res = await Post(c, "auth/login", token, ("address", "arun"), ("password", Password)).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        return res;
    }

    // As if more than five minutes had passed since the password was given.
    private void ForgetProofs()
    {
        SessionRegistry sessions = this.app.Services.GetRequiredService<SessionRegistry>();
        var proofs = (ConcurrentDictionary<string, DateTimeOffset>)typeof(SessionRegistry).GetField("proofs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sessions)!;
        proofs.Clear();
    }

    private List<string> Subjects() => this.store.MailboxMessages.Where(m => m.MailboxId == this.arun.Id).Select(m => m.Subject).ToList();

    private HttpClient Client(string userAgent)
    {
        var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(this.baseAddress + "/") };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        this.clients.Add(c);
        return c;
    }

    private async Task<HttpClient> SignedInAsync(string userAgent)
    {
        HttpClient c = this.Client(userAgent);
        Assert.Equal("/dashboard", (await SignInAsync(c)).Headers.Location!.ToString());
        return c;
    }
}
