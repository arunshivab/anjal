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
/// v1.0.0-rc.22, accounts and sign-up (owner's rules of 15-18 Oct 2026, mockups 01-09, 11): an
/// invitation makes nothing until Submit on the registration page; individuals choose their own
/// username, checked live, and must give a date of birth (18 or over); the Terms are mailed and
/// accepted at registration or at the first sign-in; the first mail is the Welcome mail; the name
/// on mails follows the organisation's rule.
/// </summary>
public sealed class Rc22AccountTests : IAsyncLifetime, IDisposable
{
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly Regex JoinRegex = new("/join/([A-Za-z0-9_-]{20,})", RegexOptions.CultureInvariant);
    private static readonly string[] ArunOnly = { "arun@sgh.in" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc22a-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly List<HttpClient> clients = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private MailboxService svc = null!;
    private string baseAddress = string.Empty;
    private TenantRow sgh = new();
    private TenantRow individuals = new();
    private MailboxRow arun = new();
    private MailboxRow manager = new();

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
        this.arun = await this.Person(this.sgh, "arun", "sgh.in", "Arun Shiva B");
        this.individuals = await this.store.UpsertTenantAsync(new TenantRow { Slug = "anjalmail", DisplayName = "Anjal", PostmasterMailbox = "usermanagement@anjalmail.test" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.individuals.Id, Domain = "anjalmail.test" });
        await TenantKind.WriteAsync(this.store, this.individuals.Id, true);
        this.manager = await this.Person(this.individuals, "usermanagement", "anjalmail.test", "User management");
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
    public async Task AnIndividual_IsInvited_ChoosesAUsername_AndRegisters_WithTheTermsAndTheWelcomeMail()
    {
        HttpClient um = this.Client();
        await SignInAsync(um, "usermanagement@anjalmail.test");
        string console = await um.GetStringAsync("org/people");
        Assert.Contains("User management", console, StringComparison.Ordinal);
        Assert.Contains("<th>Born</th>", console, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/org/signin\"", console, StringComparison.Ordinal);

        string token = Token(await um.GetStringAsync("org/people?add=1&mode=invite"));
        HttpResponseMessage res = await Post(um, "org/people/invite", token, ("first", "Nafees"), ("last", "Ahmed"), ("domain", "anjalmail.test"), ("personal", "nafees.a@gmail.test"));
        Assert.Equal("/org/people?links=1&saved=invited", res.Headers.Location!.ToString());
        string shown = await um.GetStringAsync("org/people?links=1");
        string join = JoinRegex.Match(shown).Groups[1].Value;
        Assert.True(join.Length > 20);
        Assert.Contains("Invited", shown, StringComparison.Ordinal);
        // Nothing exists yet.
        Assert.DoesNotContain(await this.store.ListMailboxesAsync(this.individuals.Id), m => m.LocalPart != "usermanagement");
        OutboundMessage invitation = Assert.Single(await this.store.ListOutboundForSenderAsync(string.Empty));
        Assert.Equal("nafees.a@gmail.test", invitation.EnvelopeTo);
        Assert.Contains("/join/" + join, System.Text.Encoding.UTF8.GetString(invitation.RawBytes), StringComparison.Ordinal);

        HttpClient nafees = this.Client();
        string page = await nafees.GetStringAsync("join/" + join);
        Assert.Contains("Register your mail", page, StringComparison.Ordinal);
        Assert.Contains("@anjalmail.test", page, StringComparison.Ordinal);
        Assert.Contains("type=\"date\"", page, StringComparison.Ordinal);

        // The live username check: taken, with suggestions; free.
        string taken = await nafees.GetStringAsync("auth/username?local=usermanagement&token=" + join + "&first=Nafees&last=Ahmed");
        Assert.Contains("\"ok\":false", taken, StringComparison.Ordinal);
        string free = await nafees.GetStringAsync("auth/username?local=nafees&token=" + join);
        Assert.Contains("\"ok\":true", free, StringComparison.Ordinal);
        Assert.Contains("nafees@anjalmail.test", free, StringComparison.Ordinal);

        // Under 18: refused.
        string joinToken = Token(page);
        res = await Post(nafees, "auth/join", joinToken, ("token", join), ("first", "Nafees"), ("last", "Ahmed"), ("phone", "+91 98400 12345"),
            ("dob", DateTime.UtcNow.AddYears(-17).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)), ("local", "nafees"), ("password", "meadow-copper-saffron-orchid-77"), ("terms", "1"), ("disclaimer", "1"));
        Assert.StartsWith("/join/" + join + "?e=", res.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Null(await this.store.GetMailboxAsync("nafees", "anjalmail.test"));
        string refused = await nafees.GetStringAsync(res.Headers.Location!.ToString());
        Assert.Contains("18 or over", refused, StringComparison.Ordinal);

        // Submit: the mailbox is made now, the Terms go to the personal address, the Welcome mail is in the Inbox, and they are signed in.
        res = await Post(nafees, "auth/join", joinToken, ("token", join), ("first", "Nafees"), ("last", "Ahmed"), ("phone", "+91 98400 12345"),
            ("dob", "1991-03-14"), ("local", "nafees"), ("password", "meadow-copper-saffron-orchid-77"), ("terms", "1"), ("disclaimer", "1"));
        Assert.Equal("/settings/security/two-step?welcome=1", res.Headers.Location!.ToString());
        MailboxRow made = (await this.store.GetMailboxAsync("nafees", "anjalmail.test"))!;
        Assert.Equal("Nafees Ahmed", made.DisplayName);
        PersonProfile profile = await this.svc.ProfileOfAsync(made.Id);
        Assert.Equal(new DateOnly(1991, 3, 14), profile.DateOfBirth);
        Assert.Equal("+91 98400 12345", profile.Phone);
        Assert.NotNull(profile.TermsAcceptedAt);
        Assert.True(profile.Individual);
        Assert.Null(await this.svc.SignInDemandAsync(made.Id));
        Assert.Contains(await this.store.ListOutboundForSenderAsync(string.Empty), m => m.EnvelopeTo == "nafees.a@gmail.test" && System.Text.Encoding.UTF8.GetString(m.RawBytes).Contains("Terms of Service", StringComparison.Ordinal));
        FolderRow inbox = (await this.store.ListFoldersAsync(made.Id)).Single(f => f.Name == FolderRow.Inbox);
        MessageRow welcome = Assert.Single(await this.store.ListMessagesAsync(made.Id, inbox.Id, 10, 0));
        Assert.Equal("Welcome to Anjal, Nafees", welcome.Subject);
        Assert.Contains("welcome@anjalmail.test", welcome.FromHeader, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await nafees.GetAsync("folder/INBOX")).StatusCode);
        // The link works once.
        Assert.Contains("cannot be used", await this.Client().GetStringAsync("join/" + join), StringComparison.Ordinal);
        // An individual may choose the name on their mails.
        Assert.Null(await this.svc.SetNameOnMailsAsync(made.Id, "Nafees"));
        // Listed with phone and date of birth.
        string list = await um.GetStringAsync("org/people");
        Assert.Contains("91 98400 12345", list, StringComparison.Ordinal);
        Assert.Contains("14 Mar 1991", list, StringComparison.Ordinal);
        Assert.Contains("nafees@anjalmail.test", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAdministrator_FillsEverythingIn_AndThePerson_AcceptsTheTerms_AtTheFirstSignIn()
    {
        HttpClient admin = this.Client();
        await SignInAsync(admin, "arun@sgh.in");
        string token = Token(await admin.GetStringAsync("org/people?add=1&mode=fill"));
        HttpResponseMessage res = await Post(admin, "org/people/add", token, ("first", "Nandini"), ("last", "Krishnan"), ("local", "nandini.krishnan"), ("domain", "sgh.in"),
            ("personal", "nandini.k@gmail.test"), ("phone", string.Empty), ("dob", string.Empty), ("password", "quartz-willow-zephyr-candle-19"), ("name", "Nandu"));
        Assert.EndsWith("&saved=added", res.Headers.Location!.ToString(), StringComparison.Ordinal);
        MailboxRow made = (await this.store.GetMailboxAsync("nandini.krishnan", "sgh.in"))!;
        // Organisation default: first name + last name; nicknames are off, so "Nandu" was not used.
        Assert.Equal("Nandini Krishnan", made.DisplayName);
        Assert.Equal("terms", await this.svc.SignInDemandAsync(made.Id));

        HttpClient nandini = this.Client();
        await SignInAsync(nandini, "nandini.krishnan@sgh.in", "quartz-willow-zephyr-candle-19");
        HttpResponseMessage inbox = await nandini.GetAsync("folder/INBOX");
        Assert.Equal(HttpStatusCode.Redirect, inbox.StatusCode);
        Assert.Equal("/terms/accept", inbox.Headers.Location!.ToString());
        string terms = await nandini.GetStringAsync("terms/accept");
        Assert.Contains("Before you start", terms, StringComparison.Ordinal);
        Assert.Contains("Anjal Terms of Service", terms, StringComparison.Ordinal);
        string t2 = Token(terms);
        res = await Post(nandini, "auth/terms", t2, ("terms", "1"));
        Assert.StartsWith("/terms/accept?e=", res.Headers.Location!.ToString(), StringComparison.Ordinal);
        res = await Post(nandini, "auth/terms", t2, ("terms", "1"), ("disclaimer", "1"));
        Assert.DoesNotContain("terms", res.Headers.Location!.ToString(), StringComparison.Ordinal);
        // Then the password must be changed (the administrator chose the first one).
        Assert.Equal("password", await this.svc.SignInDemandAsync(made.Id));
        Assert.Equal("/settings/security?change=1", (await nandini.GetAsync("folder/INBOX")).Headers.Location!.ToString());
        // Terms waiting is recorded.
        PersonProfile profile = await this.svc.ProfileOfAsync(made.Id);
        Assert.NotNull(profile.TermsAcceptedAt);
        Assert.Equal(LegalText.Version, profile.TermsVersion);
        // The Terms went to the personal address; the Welcome mail is in the Inbox.
        Assert.Contains(await this.store.ListOutboundForSenderAsync(string.Empty), m => m.EnvelopeTo == "nandini.k@gmail.test");
        FolderRow inboxFolder = (await this.store.ListFoldersAsync(made.Id)).Single(f => f.Name == FolderRow.Inbox);
        Assert.Contains(this.store.Seen(), m => m.MailboxId == made.Id && m.FolderId == inboxFolder.Id && m.Subject.StartsWith("Welcome to Anjal", StringComparison.Ordinal));
        // Name on mails: locked to first + last unless the organisation allows nicknames.
        Assert.NotNull(await this.svc.SetNameOnMailsAsync(made.Id, "Nandu"));
        await this.svc.SaveMailPolicyAsync(this.sgh.Id, new OrgMailPolicy { Nicknames = true });
        Assert.Null(await this.svc.SetNameOnMailsAsync(made.Id, "Nandu"));
        Assert.Equal("Nandu", (await this.store.GetMailboxByIdAsync(made.Id))!.DisplayName);
    }

    [Fact]
    public void TheRules_AgeUsernamePhone()
    {
        DateOnly today = new(2026, 10, 18);
        Assert.NotNull(MailboxService.AgeProblem(null, true, today));
        Assert.Null(MailboxService.AgeProblem(null, false, today));
        Assert.NotNull(MailboxService.AgeProblem(new DateOnly(2008, 10, 19), true, today));
        Assert.Null(MailboxService.AgeProblem(new DateOnly(2008, 10, 18), true, today));
        Assert.NotNull(MailboxService.AgeProblem(new DateOnly(2030, 1, 1), false, today));
        Assert.Null(MailboxService.UsernameProblem("priya.natarajan", true));
        Assert.NotNull(MailboxService.UsernameProblem("pr", true));
        Assert.NotNull(MailboxService.UsernameProblem(".priya", true));
        Assert.NotNull(MailboxService.UsernameProblem("priya..n", true));
        Assert.NotNull(MailboxService.UsernameProblem("postmaster", true));
        Assert.NotNull(MailboxService.UsernameProblem("priya natarajan", true));
        Assert.Null(MailboxService.PhoneProblem("+91 98400 12345"));
        Assert.Null(MailboxService.PhoneProblem(string.Empty));
        Assert.NotNull(MailboxService.PhoneProblem("call me"));
    }

    [Fact]
    public async Task ThePublicTermsPages_AreOpen_AndTheMailPolicy_IsSaved()
    {
        HttpClient anyone = this.Client();
        string terms = await anyone.GetStringAsync("terms");
        Assert.Contains("Terms of Service", terms, StringComparison.Ordinal);
        Assert.Contains("18 or over", terms, StringComparison.Ordinal);
        Assert.Contains("Disclaimer", await anyone.GetStringAsync("disclaimer"), StringComparison.Ordinal);

        HttpClient admin = this.Client();
        await SignInAsync(admin, "arun@sgh.in");
        string page = await admin.GetStringAsync("org/rules");
        Assert.Contains("Forwarding outside the organisation", page, StringComparison.Ordinal);
        HttpResponseMessage res = await Post(admin, "org/rules", Token(page), ("forward", "1"), ("nicknames", "1"), ("away", "0"));
        Assert.Equal("/org/rules?saved=1", res.Headers.Location!.ToString());
        OrgMailPolicy policy = await this.svc.MailPolicyOfAsync(this.sgh.Id);
        Assert.True(policy.ForwardingOutside);
        Assert.True(policy.Nicknames);
        Assert.False(policy.AwayOutside);
    }

    [Fact]
    public async Task TwoAccounts_SignedInTogether_SwitchFromTheInitialsMenu_AndSignOutOneOrBoth()
    {
        MailboxRow meera = await this.Person(this.sgh, "meera", "sgh.in", "Meera Iyer");
        HttpClient browser = this.Client();
        await SignInAsync(browser, "arun@sgh.in");
        string inbox = await browser.GetStringAsync("folder/INBOX");
        Assert.Contains("Add another account", inbox, StringComparison.Ordinal);
        Assert.DoesNotContain("Switch to", inbox, StringComparison.Ordinal);

        // Add another account: the first stays signed in.
        string page = await browser.GetStringAsync("sign-in?add=1");
        Assert.Contains("Add another account", page, StringComparison.Ordinal);
        HttpResponseMessage res = await Post(browser, "auth/login", Token(page), ("address", "meera@sgh.in"), ("password", "correct horse battery"), ("add", "1"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        inbox = await browser.GetStringAsync("folder/INBOX");
        Assert.Contains("meera@sgh.in</span>", inbox, StringComparison.Ordinal);
        Assert.Contains("Switch to", inbox, StringComparison.Ordinal);
        Assert.Contains("arun@sgh.in", inbox, StringComparison.Ordinal);
        Assert.Contains("Sign out of both", inbox, StringComparison.Ordinal);

        // Switch back to Arun; Meera stays signed in.
        res = await Post(browser, "auth/switch", Token(inbox), ("to", this.arun.Id.ToString()), ("back", "/folder/INBOX"));
        Assert.Equal("/folder/INBOX", res.Headers.Location!.ToString());
        inbox = await browser.GetStringAsync("folder/INBOX");
        Assert.Contains("arun@sgh.in</span>", inbox, StringComparison.Ordinal);
        Assert.Contains("meera@sgh.in", inbox, StringComparison.Ordinal);
        Assert.Equal(2, (await this.app.Services.GetRequiredService<SessionRegistry>().ListAsync(meera.Id, null)).Count + (await this.app.Services.GetRequiredService<SessionRegistry>().ListAsync(this.arun.Id, null)).Count);

        // Sign out of this account: the other opens.
        res = await Post(browser, "sign-out", Token(inbox));
        Assert.Equal("/folder/INBOX?switched=1", res.Headers.Location!.ToString());
        inbox = await browser.GetStringAsync("folder/INBOX");
        Assert.Contains("meera@sgh.in</span>", inbox, StringComparison.Ordinal);
        Assert.DoesNotContain("Switch to", inbox, StringComparison.Ordinal);
        Assert.Empty(await this.app.Services.GetRequiredService<SessionRegistry>().ListAsync(this.arun.Id, null));

        // Sign out of all (here, the last one): the signed-out page.
        res = await Post(browser, "sign-out", Token(inbox), ("all", "1"));
        Assert.StartsWith("/signed-out", res.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("folder/INBOX")).StatusCode);
    }

    [Fact]
    public async Task YourData_PreparesADownload_AndRecordsADeletionRequest()
    {
        HttpClient arun = this.Client();
        await SignInAsync(arun, "arun@sgh.in");
        // Something to export.
        var sink = new MailboxSink(this.store, this.maildir);
        Assert.Equal(Anjal.Smtp.DeliveryOutcome.Accepted, (await sink.DeliverAsync(new Anjal.Smtp.DeliveryContext
        {
            EnvelopeFrom = "lab@example.test",
            EnvelopeTo = ArunOnly,
            RawBytes = System.Text.Encoding.UTF8.GetBytes("From: <lab@example.test>\r\nTo: <arun@sgh.in>\r\nSubject: Results\r\nMessage-ID: <r1@example.test>\r\n\r\nAll clear.\r\n"),
        })).Outcome);
        string page = await arun.GetStringAsync("settings/data");
        Assert.Contains("Prepare my download", page, StringComparison.Ordinal);
        Assert.Contains("Digital Personal Data Protection", page, StringComparison.Ordinal);
        ExportState state;
        // rc.25 (DES-15 §7.10): made with the person's keys (here, the organisation's), and kept encrypted on disk.
        using (Anjal.Store.MailKeyring ring = TestMail.Ring(this.store))
        using (Anjal.Store.MailKeyring.Use(ring))
        {
            state = await this.svc.BuildExportAsync(this.arun.Id, this.root, "http://anjal.localhost");
        }
        Assert.True(state.Ready(DateTimeOffset.UtcNow), state.Error);
        Assert.Equal(1, state.Messages);
        Assert.True(Anjal.Store.SealedFile.IsSealed(File.ReadAllBytes(state.File)));
        using var opened = new MemoryStream();
        using (Anjal.Store.MailKeyring ring = TestMail.Ring(this.store))
        await using (FileStream sealedFile = File.OpenRead(state.File))
        {
            await Anjal.Store.SealedFile.OpenToAsync(sealedFile, opened, ring);
        }
        opened.Position = 0;
        using (var zip = new System.IO.Compression.ZipArchive(opened, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.Contains(zip.Entries, e => e.FullName.StartsWith("mail/INBOX/", StringComparison.Ordinal) && e.FullName.EndsWith(".eml", StringComparison.Ordinal));
            Assert.Contains(zip.Entries, e => e.FullName == "contacts.vcf");
            Assert.Contains(zip.Entries, e => e.FullName == "profile.json");
            Assert.Contains(zip.Entries, e => e.FullName == "activity.csv");
            Assert.Contains(zip.Entries, e => e.FullName == "README.txt");
        }
        HttpResponseMessage download = await arun.GetAsync("settings/data/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/zip", download.Content.Headers.ContentType!.MediaType);
        page = await arun.GetStringAsync("settings/data");
        Assert.Contains("Download the zip", page, StringComparison.Ordinal);
        // The person was mailed.
        FolderRow inbox = (await this.store.ListFoldersAsync(this.arun.Id)).Single(f => f.Name == FolderRow.Inbox);
        Assert.Contains(this.store.Seen(), m => m.MailboxId == this.arun.Id && m.FolderId == inbox.Id && m.Subject == "Your data is ready to download");
        Assert.Equal(0, MailboxService.ClearOldExports(this.root, DateTimeOffset.UtcNow));
        Assert.Equal(1, MailboxService.ClearOldExports(this.root, DateTimeOffset.UtcNow.AddDays(8)));

        // Ask for deletion: recorded, the administrators told (Arun is the only admin, so nobody else), shown in the console.
        HttpResponseMessage res = await Post(arun, "settings/data/delete", Token(page), ("reason", "Leaving the hospital"));
        Assert.Equal("/settings/data?saved=delete", res.Headers.Location!.ToString());
        Assert.NotNull(await this.svc.DeletionRequestOfAsync(this.arun.Id));
        Assert.Contains("Withdraw the request", await arun.GetStringAsync("settings/data"), StringComparison.Ordinal);
        Assert.Contains("Asked to delete", await arun.GetStringAsync("org/people"), StringComparison.Ordinal);
        // The only administrator cannot be deleted; a member can, and the address is retired.
        MailboxRow meera = await this.Person(this.sgh, "meera", "sgh.in", "Meera Iyer");
        Assert.NotNull(await this.svc.DeleteAccountAsync(this.sgh, this.arun.Id, this.root));
        Assert.Null(await this.svc.DeleteAccountAsync(this.sgh, meera.Id, this.root));
        Assert.Null(await this.store.GetMailboxAsync("meera", "sgh.in"));
        Assert.Contains("meera@sgh.in", await this.svc.RetiredAddressesAsync(this.sgh.Id));
        Assert.False((await this.svc.UsernameFreeAsync(this.sgh, "meera", "sgh.in", "Meera", "Iyer")).Free);
    }

    private async Task<MailboxRow> Person(TenantRow tenant, string local, string domain, string name) =>
        await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = local,
            Domain = domain,
            DisplayName = name,
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        });

    private HttpClient Client()
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        var client = new HttpClient(handler) { BaseAddress = new Uri(this.baseAddress + "/") };
        this.clients.Add(client);
        return client;
    }

    private static async Task SignInAsync(HttpClient client, string address, string password = "correct horse battery")
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
