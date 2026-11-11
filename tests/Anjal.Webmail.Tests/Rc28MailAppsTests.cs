using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Anjal.Imap;
using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Anjal.Webmail.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Anjal.Webmail.Tests;

/// <summary>
/// rc.28 (DES-20): a password made for a mail app in Settings opens the person's sealed mail over IMAP
/// and nothing else; removing it stops it; old mail comes in from an mbox and from another IMAP server,
/// checked, dated and without duplicates.
/// </summary>
public sealed class Rc28MailAppsTests : IAsyncLifetime, IDisposable
{
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly Regex PasswordRegex = new("class=\"apppw\"[^>]*>([a-z]{4}-[a-z]{4}-[a-z]{4}-[a-z]{4})<", RegexOptions.CultureInvariant);
    private static readonly string[] ToArun = { "arun@anjal.co.in" };
    private static readonly string[] ToMeena = { "meena@old.test" };
    private static readonly string[] OldSubjects = { "Old one", "Old two" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc28m-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly List<HttpClient> clients = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private string baseAddress = string.Empty;
    private MailboxRow arun = new();
    private TenantRow tenant = new();

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
        this.tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "Imagiqa", PostmasterMailbox = "arun@anjal.co.in" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.tenant.Id, Domain = "anjal.co.in" });
        this.arun = await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = this.tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.co.in",
            DisplayName = "Arun Shiva B",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        });
        this.app = Program.CreateApp(Array.Empty<string>(), this.store, this.maildir, "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
        this.baseAddress = this.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        MailboxService.TestPlainImport = false;
        await this.app.StopAsync();
        await this.app.DisposeAsync();
        if (Directory.Exists(this.root))
        {
            TestFolders.Delete(this.root);
        }
    }

    [Fact]
    public async Task AnAppPassword_MadeInSettings_OpensSealedMailOverImap_AndRemovingItStopsIt()
    {
        HttpClient c = this.Client();
        await SignInAsync(c);
        // The keys exist after the first sign-in; a mail arrives sealed.
        await Deliver(new MailboxSink(this.store, this.maildir), "Rota", "See you at 10.");

        string settings = await c.GetStringAsync("settings/apps");
        Assert.Contains("data-mailapps", settings, StringComparison.Ordinal);
        Assert.Contains("993", settings, StringComparison.Ordinal);
        HttpResponseMessage made = await PostAsync(c, "settings/apps", "settings/apps/new", ("name", "Arun's iPhone"));
        string shown = WebUtility.HtmlDecode(await c.GetStringAsync(made.Headers.Location!.OriginalString.TrimStart('/')));
        string password = PasswordRegex.Match(shown).Groups[1].Value;
        Assert.Equal(19, password.Length);
        AppPasswordRow row = Assert.Single(await this.store.ListAppPasswordsAsync(this.arun.Id));
        Assert.NotNull(row.PrivateByApp);
        Assert.DoesNotContain(password, row.Hash, StringComparison.Ordinal);
        Assert.Single(await this.store.ListAuditAsync(50), e => e.Action == "mailapp.made");

        // The IMAP server, beside the webmail, opens the mail with that password alone.
        await using var server = new ImapServer(new ImapServerOptions { BindAddress = IPAddress.Loopback, Port = 0, ImplicitTls = false }, new StoreImapBackend(this.store, this.KeyedMaildir(), MailKeys.PepperFromEnvironment()));
        server.Start();
        MailboxService.TestPlainImport = true;
        await using (ImapPullClient imap = await ImapPullClient.ConnectAsync("127.0.0.1", server.LocalPort, tls: false, CancellationToken.None))
        {
            Assert.Null(await imap.LoginAsync("arun@anjal.co.in", password, CancellationToken.None));
            IReadOnlyList<long> uids = (await imap.UidsAsync("INBOX", 0, CancellationToken.None))!;
            PulledMessage mail = Assert.Single(await imap.FetchAsync(uids.ToArray(), CancellationToken.None));
            Assert.Contains("See you at 10.", Encoding.UTF8.GetString(mail.Raw), StringComparison.Ordinal);
            Assert.False(mail.Seen);
        }

        // The account password is refused by the IMAP server; a removed app password at once.
        await using (ImapPullClient imap = await ImapPullClient.ConnectAsync("127.0.0.1", server.LocalPort, tls: false, CancellationToken.None))
        {
            Assert.Contains("Use a password made for this app", await imap.LoginAsync("arun@anjal.co.in", "correct horse battery", CancellationToken.None), StringComparison.Ordinal);
        }
        await PostAsync(c, "settings/apps", $"settings/apps/{row.Id}/remove");
        Assert.Empty(await this.store.ListAppPasswordsAsync(this.arun.Id));
        await using (ImapPullClient imap = await ImapPullClient.ConnectAsync("127.0.0.1", server.LocalPort, tls: false, CancellationToken.None))
        {
            Assert.NotNull(await imap.LoginAsync("arun@anjal.co.in", password, CancellationToken.None));
        }
    }

    [Fact]
    public async Task AStoppedImport_StaysStopped_AndAnUnchosenLookEndsWithItsPassword()
    {
        // Review, 9 Nov: the running import saved its own copy of the job over the person's Stop.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ImportJobRow job = await this.store.AddImportJobAsync(new ImportJobRow { MailboxId = this.arun.Id, Kind = ImportJobRow.ServerKind, Source = "imap.old.test a@old.test", Secret = "sealed", State = ImportJobRow.Waiting, CreatedAt = now, UpdatedAt = now });
        Assert.True(await this.store.ClaimImportJobAsync(job.Id, now.AddMinutes(-10)));
        ImportJobRow running = (await this.store.GetImportJobAsync(job.Id))!;
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        Assert.True(await svc.StopImportAsync(this.arun.Id, job.Id));
        running.Imported = 7;
        await this.store.SaveImportJobAsync(running);
        ImportJobRow after = (await this.store.GetImportJobAsync(job.Id))!;
        Assert.Equal(ImportJobRow.Stopped, after.State);
        Assert.Equal(string.Empty, after.Secret);

        ImportJobRow look = await this.store.AddImportJobAsync(new ImportJobRow { MailboxId = this.arun.Id, Kind = ImportJobRow.ServerKind, Source = "imap.old.test a@old.test", Secret = "sealed", State = MailboxService.ImportChoosing, CreatedAt = now.AddHours(-2), UpdatedAt = now.AddHours(-2) });
        await svc.RunImportsAsync();
        ImportJobRow ended = (await this.store.GetImportJobAsync(look.Id))!;
        Assert.Equal(ImportJobRow.Stopped, ended.State);
        Assert.Equal(string.Empty, ended.Secret);
    }

    [Fact]
    public async Task AnMbox_IsBroughtIn_WithItsDates_WithoutDuplicatesOrPrograms()
    {
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        string mbox = "From a@b Mon Nov  2 10:00:00 2026\n"
            + "From: <ravi@old.test>\nSubject: First\nDate: Mon, 2 Nov 2026 10:00:00 +0530\nMessage-ID: <one@old.test>\n\nHello.\n>From the archive.\n\n"
            + "From a@b Mon Nov  2 11:00:00 2026\n"
            + "From: <ravi@old.test>\nSubject: First again\nDate: Mon, 2 Nov 2026 11:00:00 +0530\nMessage-ID: <one@old.test>\n\nSame id.\n\n"
            + "From a@b Mon Nov  2 12:00:00 2026\n"
            + "From: <ravi@old.test>\nSubject: Second\nDate: Tue, 3 Nov 2026 09:30:00 +0530\nMessage-ID: <two@old.test>\nContent-Type: multipart/mixed; boundary=\"B\"\n\n--B\nContent-Type: text/plain\n\nSee file.\n--B\nContent-Type: application/octet-stream; name=\"setup.exe\"\nContent-Disposition: attachment; filename=\"setup.exe\"\n\nMZ\n--B--\n\n"
            + "From a@b Mon Nov  2 13:00:00 2026\n"
            + "From: <ravi@old.test>\nSubject: Third\nDate: Wed, 4 Nov 2026 08:00:00 +0530\nMessage-ID: <three@old.test>\n\nLast one.\n";
        using var upload = new MemoryStream(Encoding.UTF8.GetBytes(mbox));
        Assert.Null(await svc.StartFileImportAsync(this.arun.Id, upload, "old.mbox", null));
        ImportJobRow job = await DoneAsync(svc, this.arun.Id);
        Assert.Equal(ImportJobRow.Done, job.State);
        Assert.Equal(2, job.Imported);
        Assert.Equal(2, job.Skipped);
        FolderRow imported = await this.store.EnsureFolderAsync(this.arun.Id, MailboxService.ImportedFolder);
        IReadOnlyList<MessageRow> rows = await this.store.ListMessagesAsync(this.arun.Id, imported.Id, 10, 0);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.ReceivedAt == new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(5.5)));
        Assert.All(rows, r => Assert.True(r.Seen));
        Assert.False(Directory.EnumerateFiles(Path.Combine(this.root, "mail", ".imports"), "*.upload", SearchOption.AllDirectories).Any());
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.FromHours(5.5)), MailboxService.HeaderDate("Mon, 9 Nov 2026 10:00:00 +0530 (IST)"));
    }

    [Fact]
    public async Task MailFromAnotherServer_IsCopiedFolderByFolder_AndTheOldServerIsLeftAsItWas()
    {
        // The "other server" is a second Anjal with its own store: Meena's old mailbox, two mails.
        var oldStore = new InMemoryMessageStore();
        var oldMaildir = new MaildirStore(Path.Combine(this.root, "old"), "old");
        TenantRow oldTenant = await oldStore.UpsertTenantAsync(new TenantRow { Slug = "people", DisplayName = "Individuals", PostmasterMailbox = "meena@old.test" });
        await oldStore.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = oldTenant.Id, Domain = "old.test" });
        MailboxRow meena = await oldStore.UpsertMailboxAsync(new MailboxRow { TenantId = oldTenant.Id, LocalPart = "meena", Domain = "old.test", DisplayName = "Meena", PasswordPbkdf2 = Pbkdf2Hasher.Hash("old password", 1000) });
        string oldApp = AppPasswords.NewPassword();
        await oldStore.AddAppPasswordAsync(AppPasswords.Make(meena.Id, "Anjal import", oldApp, null, Array.Empty<byte>(), DateTimeOffset.UtcNow));
        var oldSink = new MailboxSink(oldStore, oldMaildir);
        foreach (string subject in OldSubjects)
        {
            await oldSink.DeliverAsync(new DeliveryContext { EnvelopeFrom = "x@y.test", EnvelopeTo = ToMeena, TransportTls = "TLS 1.3", RawBytes = Encoding.UTF8.GetBytes($"From: <x@y.test>\r\nTo: <meena@old.test>\r\nSubject: {subject}\r\nMessage-ID: <{Guid.NewGuid():N}@y.test>\r\n\r\nText.\r\n") });
        }
        await using var oldServer = new ImapServer(new ImapServerOptions { BindAddress = IPAddress.Loopback, Port = 0, ImplicitTls = false }, new StoreImapBackend(oldStore, oldMaildir, Array.Empty<byte>()));
        oldServer.Start();

        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        MailboxService.TestPlainImport = true;
        await this.store.AddImportJobAsync(new ImportJobRow
        {
            MailboxId = this.arun.Id,
            Kind = ImportJobRow.ServerKind,
            Source = "127.0.0.1:" + oldServer.LocalPort + " meena@old.test",
            Secret = svc.Protector!.Protect(oldApp),
            Folders = "INBOX",
            State = ImportJobRow.Waiting,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        ImportJobRow job = await DoneAsync(svc, this.arun.Id);
        Assert.Equal(ImportJobRow.Done, job.State);
        Assert.Equal(2, job.Imported);
        Assert.Equal(string.Empty, job.Secret);
        FolderRow folder = await this.store.EnsureFolderAsync(this.arun.Id, "Imported - INBOX");
        Assert.Equal(2, (await this.store.ListMessagesAsync(this.arun.Id, folder.Id, 10, 0)).Count);
        // Read with PEEK: still unread at the old server.
        Assert.All(await oldStore.ListMessagesAsync(meena.Id, null, 10, 0), m => Assert.False(m.Seen));
        Assert.Equal("Imported - Gmail_Sent Mail", MailboxService.ImportFolderName("[Gmail]/Sent Mail", 2));
        Assert.Equal("Imported - folder 3", MailboxService.ImportFolderName("நோயாளிகள்", 3));
    }

    // The test runs the import itself; the webmail's own worker may take it first - either way, wait for the end.
    private static async Task<ImportJobRow> DoneAsync(MailboxService svc, Guid mailboxId)
    {
        for (int i = 0; i < 300; i++)
        {
            await svc.RunImportsAsync();
            ImportJobRow job = Assert.Single(await svc.ImportJobsAsync(mailboxId));
            if (job.State is ImportJobRow.Done or ImportJobRow.Failed)
            {
                return job;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("The import did not end.");
    }

    private MaildirStore KeyedMaildir()
    {
        var m = new MaildirStore(Path.Combine(this.root, "mail"), "test") { Keys = new MailboxKeyDirectory(this.store, this.store) };
        return m;
    }

    private static async Task Deliver(MailboxSink sink, string subject, string body)
    {
        DeliveryResult r = await sink.DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "priya@gmail.test",
            EnvelopeTo = ToArun,
            TransportTls = "TLS 1.3",
            RawBytes = Encoding.UTF8.GetBytes($"From: <priya@gmail.test>\r\nTo: <arun@anjal.co.in>\r\nSubject: {subject}\r\nMessage-ID: <{Guid.NewGuid():N}@gmail.test>\r\n\r\n{body}\r\n"),
        });
        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
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

    private static async Task SignInAsync(HttpClient c)
    {
        string token = Token(await c.GetStringAsync("sign-in"));
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["address"] = "arun",
            ["password"] = "correct horse battery",
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
