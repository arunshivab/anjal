using System.Net;
using System.Text;
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
/// rc.28 (DES-19, item 74): a secure link instead of the mail for people outside - the notice carries no
/// word of the mail, the database cannot open it, a code mailed to the person opens it for a visit, five
/// wrong codes stop it, a reply reaches the sender marked, and the warning's other choice sends to the
/// people inside only.
/// </summary>
public sealed class Rc28SecureLinkTests : IAsyncLifetime, IDisposable
{
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly Regex LinkRegex = new("/s/([A-Za-z0-9_-]{43})", RegexOptions.CultureInvariant);
    private static readonly Regex CodeRegex = new("(\\d{3}) (\\d{3})", RegexOptions.CultureInvariant);

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc28s-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly List<HttpClient> clients = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private string baseAddress = string.Empty;
    private MailboxRow arun = new();
    private MailboxRow meena = new();

    public void Dispose()
    {
        foreach (HttpClient c in this.clients)
        {
            c.Dispose();
        }
    }

    public async Task InitializeAsync()
    {
        this.maildir = new MaildirStore(Path.Combine(this.root, "mail"), "test");
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "Imagiqa", PostmasterMailbox = "arun@anjal.co.in" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        this.arun = await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.co.in",
            DisplayName = "Arun Shiva B",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        });
        this.meena = await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = "meena",
            Domain = "anjal.co.in",
            DisplayName = "Meena",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("another long phrase", 1000),
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
            TestFolders.Delete(this.root);
        }
    }

    [Fact]
    public void TheSeal_OpensOnlyWithItsToken_AndCodesAreKeyedToIt()
    {
        string token = SecureLinkCrypto.NewToken();
        Assert.Equal(43, token.Length);
        byte[] mail = "Subject: Discharge\r\n\r\nGoes home today.\r\n"u8.ToArray();
        byte[] sealedMail = SecureLinkCrypto.Seal(token, mail);
        Assert.Equal(mail, SecureLinkCrypto.Open(token, sealedMail));
        Assert.Null(SecureLinkCrypto.Open(SecureLinkCrypto.NewToken(), sealedMail));
        Assert.Null(SecureLinkCrypto.Open("not a token", sealedMail));
        Assert.DoesNotContain("Discharge", Encoding.ASCII.GetString(sealedMail), StringComparison.Ordinal);
        Assert.Equal(64, SecureLinkCrypto.Hash(token).Length);
        Assert.Equal(string.Empty, SecureLinkCrypto.Hash("short"));
        string code = SecureLinkCrypto.NewCode();
        Assert.Matches("^\\d{6}$", code);
        Assert.True(SecureLinkCrypto.Same(SecureLinkCrypto.CodeHash(token, code), SecureLinkCrypto.CodeHash(token, code[..3] + " " + code[3..])));
        Assert.False(SecureLinkCrypto.Same(SecureLinkCrypto.CodeHash(token, code), SecureLinkCrypto.CodeHash(SecureLinkCrypto.NewToken(), code)));
    }

    [Fact]
    public async Task ASecureLink_CarriesNoWordOfTheMail_OpensWithTheMailedCode_AndTheReplyReachesTheSender()
    {
        HttpClient c = this.Client();
        await SignInAsync(c, "arun", "correct horse battery");

        HttpResponseMessage sent = await ComposeAsync(c, "priya@gmail.test", "Discharge summary for Priya", "She goes home today.", ("secure", "1"));
        Assert.Equal("/folder/Sent?sent=1", sent.Headers.Location!.OriginalString);

        // The notice: from Arun, the link, nothing of the mail.
        OutboundMessage notice = Assert.Single(await this.store.ListOutboundForSenderAsync("arun@anjal.co.in"));
        Assert.Equal("priya@gmail.test", notice.EnvelopeTo);
        string noticeText = Encoding.UTF8.GetString(notice.RawBytes);
        Assert.DoesNotContain("Discharge", noticeText, StringComparison.Ordinal);
        Assert.DoesNotContain("goes home", noticeText, StringComparison.Ordinal);
        string token = LinkRegex.Match(noticeText).Groups[1].Value;
        Assert.Equal(43, token.Length);

        // The database keeps neither the token nor the mail's words.
        SecureLinkRow link = (await this.store.GetSecureLinkByTokenAsync(SecureLinkCrypto.Hash(token)))!;
        Assert.DoesNotContain("goes home", Encoding.UTF8.GetString(link.Sealed), StringComparison.Ordinal);
        Assert.Single(await this.Log("securelink.made"));

        // The outside person: the page shows nothing of the mail before the code.
        HttpClient outside = this.Client();
        string first = WebUtility.HtmlDecode(await outside.GetStringAsync("s/" + token));
        Assert.Contains("p•••@gmail.test", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Discharge", first, StringComparison.Ordinal);
        await PostAsync(outside, "s/" + token, "s/" + token + "/code");
        OutboundMessage codeMail = (await this.store.ListOutboundForSenderAsync(string.Empty)).Last(o => o.EnvelopeTo == "priya@gmail.test");
        string code = CodeRegex.Match(Encoding.UTF8.GetString(codeMail.RawBytes)).Value.Replace(" ", string.Empty, StringComparison.Ordinal);

        // A wrong code says how many tries are left; the right one opens the mail.
        HttpResponseMessage wrong = await PostAsync(outside, "s/" + token + "?sent=1", "s/" + token + "/open", ("code", code == "000000" ? "111111" : "000000"));
        Assert.Contains("left=4", wrong.Headers.Location!.OriginalString, StringComparison.Ordinal);
        await PostAsync(outside, "s/" + token + "?sent=1", "s/" + token + "/open", ("code", code));
        string opened = WebUtility.HtmlDecode(await outside.GetStringAsync("s/" + token));
        Assert.Contains("Discharge summary for Priya", opened, StringComparison.Ordinal);
        Assert.Contains("goes home today", opened, StringComparison.Ordinal);
        Assert.Single(await this.Log("securelink.opened"));

        // A code works once: another browser with the same code gets nothing.
        HttpClient stranger = this.Client();
        await PostAsync(stranger, "s/" + token + "?sent=1", "s/" + token + "/open", ("code", code));
        Assert.DoesNotContain("goes home today", await stranger.GetStringAsync("s/" + token), StringComparison.Ordinal);

        // The reply: in Arun's Inbox, marked as through the link.
        using var reply = new MultipartFormDataContent
        {
            { new StringContent(Token(await outside.GetStringAsync("s/" + token))), "__RequestVerificationToken" },
            { new StringContent("Thank you, doctor."), "text" },
            { new ByteArrayContent("%PDF-1.4 my report"u8.ToArray()), "files", "report.pdf" },
        };
        HttpResponseMessage replied = await outside.PostAsync("s/" + token + "/reply", reply);
        Assert.EndsWith("?replied=1", replied.Headers.Location!.OriginalString, StringComparison.Ordinal);
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        FolderRow inbox = await this.store.EnsureFolderAsync(this.arun.Id, "INBOX");
        MessageRow answer = Assert.Single(await this.store.ListMessagesAsync(this.arun.Id, inbox.Id, 10, 0));
        // Arun's own key opens it in his reader: the envelope says it came through the link, with its file.
        string read = await c.GetStringAsync("folder/INBOX?open=" + answer.Id);
        Assert.Contains("data-secure-reply", read, StringComparison.Ordinal);
        Assert.Contains("report.pdf", read, StringComparison.Ordinal);
        Assert.True(await svc.IsSecureReplyAsync(this.arun.Id, link.Id.ToString("N"), "priya@gmail.test"));
        // Review, 9 Nov: a visit gives one reply, even with its cookie sent again; and a header alone,
        // in mail that came by SMTP, earns no badge (the reader also checks how the mail arrived).
        DateTimeOffset visitStart = DateTimeOffset.UtcNow.AddMinutes(-5);
        Assert.Equal("This visit has sent its reply. To reply again, ask for a new code.", await svc.SecureReplyAsync(token, "Again", Array.Empty<(string, string, byte[])>(), "198.51.100.7", visitStart));
        Assert.Single(await this.store.ListMessagesAsync(this.arun.Id, inbox.Id, 10, 0));
        Assert.Equal(MailboxService.SecureReplyTransport, answer.TransportTls);
        Assert.False(await svc.IsSecureReplyAsync(this.meena.Id, link.Id.ToString("N"), "priya@gmail.test"));
        Assert.False(await svc.IsSecureReplyAsync(this.arun.Id, link.Id.ToString("N"), "someone@else.test"));

        // The sender's Sent copy shows the link and where it stands.
        FolderRow sentFolder = await this.store.EnsureFolderAsync(this.arun.Id, "Sent");
        MessageRow sentCopy = Assert.Single(await this.store.ListMessagesAsync(this.arun.Id, sentFolder.Id, 10, 0));
        string sentView = WebUtility.HtmlDecode(await c.GetStringAsync("folder/Sent?open=" + sentCopy.Id));
        Assert.Contains("data-secure-links", sentView, StringComparison.Ordinal);
        Assert.Contains("Replied", sentView, StringComparison.Ordinal);
        Assert.Single(await this.Log("securelink.replied"));

        // The log names the domain, never the words.
        Assert.All((await this.store.ListAuditAsync(200)).Where(e => e.Action.StartsWith("securelink.", StringComparison.Ordinal)), e =>
        {
            Assert.Equal("gmail.test", e.Subject);
            Assert.DoesNotContain("Discharge", e.Detail, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task FiveWrongCodes_StopTheLink_AndTheSenderIsTold_StoppedAndEndedLinksShowNothing()
    {
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        (string token, SecureLinkRow link) = await this.MakeLinkAsync(DateTimeOffset.UtcNow.AddDays(7));
        Assert.Null(await svc.MailSecureCodeAsync(token, "198.51.100.7"));
        for (int i = 1; i <= MailboxService.SecureMaxWrong; i++)
        {
            (bool right, int left) = await svc.CheckSecureCodeAsync(token, "12345", "198.51.100.7");
            Assert.False(right);
            Assert.Equal(MailboxService.SecureMaxWrong - i, left);
        }
        Assert.NotNull((await this.store.GetSecureLinkAsync(link.Id))!.LockedAt);
        Assert.Null(await svc.OpenSecureMailAsync(token));
        Assert.Equal(MailboxService.SecureEnded, await svc.MailSecureCodeAsync(token, "198.51.100.7"));
        Assert.Single(await this.Log("securelink.locked"));
        FolderRow inbox = await this.store.EnsureFolderAsync(this.arun.Id, "INBOX");
        Assert.Contains(await this.store.ListMessagesAsync(this.arun.Id, inbox.Id, 10, 0), m => m.Subject.Contains("stopped after wrong codes", StringComparison.Ordinal));

        // Review, 9 Nov: tries sent at once are each counted - five, however they arrive.
        (string rushToken, SecureLinkRow rush) = await this.MakeLinkAsync(DateTimeOffset.UtcNow.AddDays(7));
        Assert.Null(await svc.MailSecureCodeAsync(rushToken, "198.51.100.8"));
        (bool Right, int Left)[] all = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => svc.CheckSecureCodeAsync(rushToken, "999999", "198.51.100.8")));
        Assert.DoesNotContain(all, r => r.Right);
        SecureLinkRow rushed = (await this.store.GetSecureLinkAsync(rush.Id))!;
        Assert.Equal(MailboxService.SecureMaxWrong, rushed.WrongCodes);
        Assert.NotNull(rushed.LockedAt);

        // Ended by time: nothing opens, and a day later the sealed copy is gone.
        (string oldToken, SecureLinkRow old) = await this.MakeLinkAsync(DateTimeOffset.UtcNow.AddDays(-2));
        Assert.Null(await svc.OpenSecureMailAsync(oldToken));
        HttpClient c = this.Client();
        Assert.Contains("This link has ended.", await c.GetStringAsync("s/" + oldToken), StringComparison.Ordinal);
        Assert.True(await svc.PurgeSecureLinksAsync(DateTimeOffset.UtcNow) >= 1);
        Assert.Empty((await this.store.GetSecureLinkAsync(old.Id))!.Sealed);

        // Stopped by the sender: only the sender's own mailbox can stop it.
        (string stopToken, SecureLinkRow stop) = await this.MakeLinkAsync(DateTimeOffset.UtcNow.AddDays(7));
        Assert.False(await svc.StopSecureLinkAsync(this.meena.Id, stop.Id, "meena@anjal.co.in"));
        Assert.True(await svc.StopSecureLinkAsync(this.arun.Id, stop.Id, "arun@anjal.co.in"));
        Assert.Null(await svc.OpenSecureMailAsync(stopToken));

        // A made-up token is no link at all.
        Assert.Null(await svc.SecureLinkPageAsync(SecureLinkCrypto.NewToken()));
    }

    [Fact]
    public async Task RemoveThem_SendsToThePeopleInsideOnly_AndIsLogged()
    {
        HttpClient c = this.Client();
        await SignInAsync(c, "arun", "correct horse battery");
        HttpResponseMessage warned = await ComposeAsync(c, "priya@gmail.test, meena@anjal.co.in", "Discharge summary for the patient", "The patient goes home today.");
        Assert.Contains("patient=", warned.Headers.Location!.OriginalString, StringComparison.Ordinal);
        string page = await c.GetStringAsync(warned.Headers.Location!.OriginalString.TrimStart('/'));
        Assert.Contains("Remove them and send to the rest", page, StringComparison.Ordinal);
        Assert.Contains("Send a secure link instead", page, StringComparison.Ordinal);

        HttpResponseMessage sent = await ComposeAsync(c, "priya@gmail.test, meena@anjal.co.in", "Discharge summary for the patient", "The patient goes home today.", ("dropOutside", "1"));
        Assert.Equal("/folder/Sent?sent=1", sent.Headers.Location!.OriginalString);
        Assert.Empty(await this.store.ListOutboundForSenderAsync("arun@anjal.co.in"));
        FolderRow inbox = await this.store.EnsureFolderAsync(this.meena.Id, "INBOX");
        Assert.Single(await this.store.ListMessagesAsync(this.meena.Id, inbox.Id, 10, 0));
        Assert.Single(await this.Log("webmail.patient-warning.removed"));

        // Nobody inside: the choice is refused, nothing goes.
        HttpResponseMessage none = await ComposeAsync(c, "priya@gmail.test", "Discharge summary for the patient", "The patient goes home today.", ("dropOutside", "1"));
        Assert.Contains("error=", none.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Empty(await this.store.ListOutboundForSenderAsync("arun@anjal.co.in"));
    }

    [Fact]
    public async Task AMailStuckInTheOutbox_GoesAsASecureLink_AndLeavesTheOutbox()
    {
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        OutboundMessage stuck = await this.store.EnqueueOutboundAsync(new OutboundMessage
        {
            EnvelopeFrom = "arun@anjal.co.in",
            EnvelopeTo = "priya@rediffmail.test",
            RawBytes = "From: arun@anjal.co.in\r\nTo: priya@rediffmail.test\r\nSubject: Results\r\nMessage-ID: <res@anjal.co.in>\r\n\r\nYour results.\r\n"u8.ToArray(),
        });
        Assert.Null(await svc.OutboxAsSecureLinkAsync(this.arun.Id, stuck.Id, "arun@anjal.co.in", "https://mail.anjal.test"));
        Assert.DoesNotContain(await svc.ListOutboxAsync(this.arun.Id), o => o.Id == stuck.Id);
        SecureLinkRow link = Assert.Single(await svc.SecureLinksOfAsync(this.arun.Id, "res@anjal.co.in"));
        Assert.Equal("priya@rediffmail.test", link.Recipient);
        OutboundMessage notice = Assert.Single(await this.store.ListOutboundForSenderAsync("arun@anjal.co.in"), o => o.Id != stuck.Id && o.EnvelopeTo == "priya@rediffmail.test");
        Assert.Contains("https://mail.anjal.test/s/", Encoding.UTF8.GetString(notice.RawBytes), StringComparison.Ordinal);
        Assert.NotNull(await svc.OutboxAsSecureLinkAsync(this.meena.Id, stuck.Id, "meena@anjal.co.in", "https://mail.anjal.test"));
    }

    private async Task<(string Token, SecureLinkRow Link)> MakeLinkAsync(DateTimeOffset expires)
    {
        string token = SecureLinkCrypto.NewToken();
        SecureLinkRow link = await this.store.AddSecureLinkAsync(new SecureLinkRow
        {
            MailboxId = this.arun.Id,
            Recipient = "priya@gmail.test",
            TokenSha256 = SecureLinkCrypto.Hash(token),
            Sealed = SecureLinkCrypto.Seal(token, "From: arun@anjal.co.in\r\nSubject: Rota\r\n\r\nSee you.\r\n"u8.ToArray()),
            MessageId = "<rota@anjal.co.in>",
            CreatedAt = expires.AddDays(-7),
            ExpiresAt = expires,
        });
        return (token, link);
    }

    private async Task<IReadOnlyList<AuditEvent>> Log(string action) =>
        (await this.store.ListAuditAsync(200)).Where(e => e.Action == action).ToList();

    private static async Task<HttpResponseMessage> ComposeAsync(HttpClient c, string to, string subject, string body, params (string Key, string Value)[] more)
    {
        string token = Token(await c.GetStringAsync("compose"));
        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent(to), "to" },
            { new StringContent(subject), "subject" },
            { new StringContent(body), "body" },
        };
        foreach ((string key, string value) in more)
        {
            form.Add(new StringContent(value), key);
        }
        return await c.PostAsync("compose", form);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient c, string page, string action, params (string Key, string Value)[] fields)
    {
        var values = new Dictionary<string, string> { ["__RequestVerificationToken"] = Token(await c.GetStringAsync(page)) };
        foreach ((string key, string value) in fields)
        {
            values[key] = value;
        }
        using var form = new FormUrlEncodedContent(values);
        return await c.PostAsync(action, form);
    }

    private static async Task SignInAsync(HttpClient c, string address, string password)
    {
        string token = Token(await c.GetStringAsync("sign-in"));
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["address"] = address,
            ["password"] = password,
        });
        Assert.Equal(HttpStatusCode.Redirect, (await c.PostAsync("auth/login", form)).StatusCode);
    }

    private static string Token(string html)
    {
        Match m = TokenRegex.Match(html);
        Assert.True(m.Success, "no antiforgery token");
        return m.Groups[1].Value;
    }

    private HttpClient Client()
    {
        var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(this.baseAddress + "/") };
        this.clients.Add(c);
        return c;
    }
}
