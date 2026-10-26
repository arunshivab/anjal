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
/// rc.25 (DES-15): organisation-held encryption through the webmail - the password opens the mail at
/// sign-in; a session without the keys is sent to the unlock page; the administrator prints and
/// confirms the recovery sheet and restores a person whose password was reset; a shared mailbox's
/// key reaches its members.
/// </summary>
public sealed class Rc25EncryptionFlowTests : IAsyncLifetime, IDisposable
{
    private const string Password = "correct horse battery";
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly Regex SheetRegex = new("<pre class=\"sheettext\"[^>]*>([^<]+)</pre>", RegexOptions.CultureInvariant);
    private static readonly string[] ArunOnly = { "arun@sgh.in" };
    private static readonly string[] EnquiriesOnly = { "enquiries@sgh.in" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc25w-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly List<HttpClient> clients = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private MailboxService svc = null!;
    private string baseAddress = string.Empty;
    private TenantRow sgh = new();
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
        this.sgh = await this.store.UpsertTenantAsync(new TenantRow { Slug = "sgh", DisplayName = "Sri Ganesh Hospital", PostmasterMailbox = "arun@sgh.in" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.sgh.Id, Domain = "sgh.in" });
        this.arun = await this.Person("arun", "Arun Shiva B");
        this.meera = await this.Person("meera", "Meera Iyer");
        this.app = Program.CreateApp(Array.Empty<string>(), this.store, this.maildir, "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
        this.svc = this.app.Services.GetRequiredService<MailboxService>();
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
    public async Task ThePasswordOpensTheMail_ASessionWithoutTheKeysIsAskedForIt_AndAWrongOneIsRefused()
    {
        HttpClient c = this.Client();
        await SignInAsync(c, "arun@sgh.in");
        Assert.NotNull(await this.store.GetMailboxKeysAsync(this.arun.Id));
        await this.DeliverAsync("Subject: Biopsy Ramesh MRN-1042\r\n\r\nThe growth is malignant.\r\n");
        MessageRow raw = this.store.MailboxMessages.Single(m => m.MailboxId == this.arun.Id && m.FromHeader.Contains("lab@", StringComparison.Ordinal));
        Assert.DoesNotContain("Ramesh", raw.Subject, StringComparison.Ordinal);
        Assert.Contains("Biopsy Ramesh MRN-1042", await c.GetStringAsync("folder/INBOX"), StringComparison.Ordinal);
        Assert.Contains("malignant", await c.GetStringAsync("message/" + raw.Id), StringComparison.Ordinal);

        // Owner, 26 Oct: the unlock page, asked for while the mail is already open, goes straight back.
        using (HttpResponseMessage open = await c.GetAsync("unlock?back=%2Ffolder%2FINBOX"))
        {
            Assert.Equal(HttpStatusCode.Redirect, open.StatusCode);
            Assert.EndsWith("/folder/INBOX", open.Headers.Location!.ToString(), StringComparison.Ordinal);
        }


        // A passkey sign-in has no password, so no keys: mail pages go to the unlock page.
        await this.store.DeleteMailboxSessionKeysAsync(this.arun.Id);
        using (HttpResponseMessage locked = await c.GetAsync("folder/INBOX"))
        {
            Assert.Equal(HttpStatusCode.Redirect, locked.StatusCode);
            Assert.StartsWith("/unlock?back=", locked.Headers.Location!.ToString(), StringComparison.Ordinal);
        }
        string unlock = await c.GetStringAsync("unlock?back=%2Ffolder%2FINBOX");
        Assert.Contains("a passkey alone cannot open it", unlock, StringComparison.Ordinal);
        using (HttpResponseMessage wrong = await Post(c, "auth/unlock", Token(unlock), ("password", "not it"), ("back", "/folder/INBOX")))
        {
            Assert.Contains("wrong=1", wrong.Headers.Location!.ToString(), StringComparison.Ordinal);
        }
        using (HttpResponseMessage right = await Post(c, "auth/unlock", Token(unlock), ("password", Password), ("back", "/folder/INBOX")))
        {
            Assert.Equal("/folder/INBOX", right.Headers.Location!.ToString());
        }
        Assert.Contains("Biopsy Ramesh MRN-1042", await c.GetStringAsync("folder/INBOX"), StringComparison.Ordinal);
        // Settings stay open while the mail is locked.
        await this.store.DeleteMailboxSessionKeysAsync(this.arun.Id);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("settings")).StatusCode);
    }

    [Fact]
    public async Task TheAdministrator_PrintsAndConfirmsTheSheet_AndRestoresAPersonWhosePasswordWasReset()
    {
        HttpClient admin = this.Client();
        await SignInAsync(admin, "arun@sgh.in");
        HttpClient m = this.Client();
        await SignInAsync(m, "meera@sgh.in");
        Assert.NotNull(await this.store.GetMailboxKeysAsync(this.meera.Id));

        string page = await admin.GetStringAsync("org/encryption");
        Assert.Contains("Your copy of the recovery key", page, StringComparison.Ordinal);
        Assert.Contains("Not printed yet", page, StringComparison.Ordinal);
        using (HttpResponseMessage made = await Post(admin, "org/encryption/sheet", Token(page)))
        {
            Assert.Equal("/org/encryption?sheet=1", made.Headers.Location!.ToString());
        }
        string shown = await admin.GetStringAsync("org/encryption?sheet=1");
        string sheet = WebUtility.HtmlDecode(SheetRegex.Match(shown).Groups[1].Value);
        Assert.Matches("^([0-9A-F]{4} ){16}[0-9A-F]{4}$", sheet);
        Assert.DoesNotContain(sheet, await admin.GetStringAsync("org/encryption"), StringComparison.Ordinal);   // shown once
        using (HttpResponseMessage bad = await Post(admin, "org/encryption/confirm", Token(shown), ("typed", sheet.Replace(sheet[..4], "0000", StringComparison.Ordinal))))
        {
            Assert.Contains("e=", bad.Headers.Location!.ToString(), StringComparison.Ordinal);
        }
        using (HttpResponseMessage ok = await Post(admin, "org/encryption/confirm", Token(shown), ("typed", sheet)))
        {
            Assert.Equal("/org/encryption?saved=sheet", ok.Headers.Location!.ToString());
        }
        TenantKeyRow keys = (await this.store.GetTenantKeysAsync(this.sgh.Id))!;
        Assert.Null(keys.RecoveryByServer);
        Assert.Contains(await this.store.ListGrantsOfAsync(this.sgh.Id, KeyGrant.OrgAdmin), g => g.HolderId == this.arun.Id);

        // Meera's password is reset without the old one: her mail locks and the administrator is told.
        Assert.Null(await this.svc.SetPasswordAsync(this.meera, "New quiet password two", null));
        Assert.Contains(this.store.Seen(sheet), x => x.MailboxId == this.arun.Id && x.Subject.Contains("needs their mail restored", StringComparison.Ordinal));
        HttpClient m2 = this.Client();
        await SignInAsync(m2, "meera@sgh.in", "New quiet password two");
        Assert.StartsWith("/unlock", (await m2.GetAsync("folder/INBOX")).Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Contains("Your mail is locked", await m2.GetStringAsync("unlock"), StringComparison.Ordinal);

        page = await admin.GetStringAsync("org/encryption");
        Assert.Contains("Meera Iyer", page, StringComparison.Ordinal);
        using (HttpResponseMessage restored = await Post(admin, "org/encryption/restore", Token(page), ("person", this.meera.Id.ToString())))
        {
            Assert.Equal("/org/encryption?saved=restored", restored.Headers.Location!.ToString());
        }
        string again = await m2.GetStringAsync("unlock");
        Assert.Contains("Your administrator restored your mail", again, StringComparison.Ordinal);
        using (HttpResponseMessage open = await Post(m2, "auth/unlock", Token(again), ("password", "New quiet password two"), ("back", "/folder/INBOX")))
        {
            Assert.Equal("/folder/INBOX", open.Headers.Location!.ToString());
        }
        Assert.Equal(HttpStatusCode.OK, (await m2.GetAsync("folder/INBOX")).StatusCode);
        Assert.Contains(this.store.Seen(sheet), x => x.MailboxId == this.meera.Id && x.Subject == "Your mail has been restored");
    }

    [Fact]
    public async Task ASharedMailboxsKey_ReachesItsMembers_WhenAnAdministratorVisits_AndLeavesThoseRemoved()
    {
        HttpClient admin = this.Client();
        await SignInAsync(admin, "arun@sgh.in");
        HttpClient m = this.Client();
        await SignInAsync(m, "meera@sgh.in");
        (string? error, Guid? id) = await this.svc.CreateSharedMailboxAsync(this.sgh, "enquiries@sgh.in", "Enquiries");
        Assert.Null(error);
        Assert.Null(await this.svc.SetSharedRightAsync(this.sgh, id!.Value, this.meera.Id, "send"));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("org/shared")).StatusCode);
        Assert.NotNull(await this.store.GetMailboxKeysAsync(id.Value));
        Assert.Contains(await this.store.ListGrantsOfAsync(id.Value, KeyGrant.SharedMember), g => g.HolderId == this.meera.Id);

        var sink = new MailboxSink(this.store, this.maildir);
        await sink.DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "patient@example.org",
            EnvelopeTo = EnquiriesOnly,
            RawBytes = Encoding.UTF8.GetBytes("From: <patient@example.org>\r\nTo: <enquiries@sgh.in>\r\nSubject: Appointment for knee pain\r\nMessage-ID: <k1@example.org>\r\n\r\nPlease call.\r\n"),
        });
        Assert.DoesNotContain("knee", this.store.MailboxMessages.Single(x => x.MailboxId == id.Value).Subject, StringComparison.Ordinal);
        string inbox = await m.GetStringAsync("folder/INBOX");
        using (HttpResponseMessage opened = await Post(m, "shared/open", Token(inbox), ("id", id.Value.ToString())))
        {
            Assert.Equal(HttpStatusCode.Redirect, opened.StatusCode);
        }
        Assert.Contains("Appointment for knee pain", await m.GetStringAsync("folder/INBOX"), StringComparison.Ordinal);

        Assert.Null(await this.svc.SetSharedRightAsync(this.sgh, id.Value, this.meera.Id, null));
        await admin.GetAsync("org/shared");
        Assert.DoesNotContain(await this.store.ListGrantsOfAsync(id.Value, KeyGrant.SharedMember), g => g.HolderId == this.meera.Id);
    }

    [Fact]
    public async Task MailScheduledInASession_IsSentAtItsTime_WithThePersonSignedOut()
    {
        HttpClient c = this.Client();
        await SignInAsync(c, "arun@sgh.in");
        (string? error, Guid? held) = await this.svc.HoldAsync(this.arun.Id, new ComposeRequest { To = "meera@sgh.in", Subject = "Ward round at nine", Body = "Bring the list." }, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Null(error);
        // The webmail's own pass, an hour later, holds nobody's keys.
        Assert.Equal(1, await this.svc.SendDueAsync(DateTimeOffset.UtcNow.AddHours(1), rescan: true));
        Assert.Contains(this.store.Seen(), m => m.MailboxId == this.meera.Id && m.Subject == "Ward round at nine");
        Assert.Contains(this.store.Seen(), m => m.MailboxId == this.arun.Id && m.Subject == "Ward round at nine" && m.FolderId != held);
    }

    private async Task DeliverAsync(string headAndBody)
    {
        var sink = new MailboxSink(this.store, this.maildir);
        DeliveryResult r = await sink.DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "lab@example.org",
            EnvelopeTo = ArunOnly,
            RawBytes = Encoding.UTF8.GetBytes("From: <lab@example.org>\r\nTo: <arun@sgh.in>\r\nMessage-ID: <" + Guid.NewGuid().ToString("N") + "@example.org>\r\n" + headAndBody),
        });
        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
    }

    private async Task<MailboxRow> Person(string local, string name) =>
        await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = this.sgh.Id,
            LocalPart = local,
            Domain = "sgh.in",
            DisplayName = name,
            PasswordPbkdf2 = Pbkdf2Hasher.Hash(Password, 1000),
            WelcomeDone = true,
        });

    private HttpClient Client()
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        var client = new HttpClient(handler) { BaseAddress = new Uri(this.baseAddress + "/") };
        this.clients.Add(client);
        return client;
    }

    private static async Task SignInAsync(HttpClient client, string address, string password = Password)
    {
        string html = await client.GetStringAsync("sign-in");
        HttpResponseMessage res = await Post(client, "auth/login", Token(html), ("address", address), ("password", password));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
    }

    private static string Token(string html) => TokenRegex.Match(html).Groups[1].Value;

    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, string token, params (string Name, string Value)[] fields)
    {
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        form.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)));
        using var content = new FormUrlEncodedContent(form);
        return await client.PostAsync(new Uri(path, UriKind.Relative), content);
    }
}
