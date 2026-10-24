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
/// v1.0.0-rc.23: the fixes from the deep-dive test report of 20-22 Oct 2026 (TST-15, DEF-118 to
/// DEF-167) and the owner's decisions D-171 to D-176 and D-186 (item 89). One test or more for
/// each fix, named after what the person sees.
/// </summary>
[Collection("Operators")]
public sealed class Rc23Tests : IAsyncLifetime, IDisposable
{
    private static readonly string[] SearchWords = { "", "a", "lab" };

    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly string[] ArunOnly = { "arun@sgh.in" };
    private static readonly string[] OurHost = { "anjal.localhost" };
    private static readonly string[] LabGroup = { "ramesh@lab.test", "priya@lab.test", "anil@lab.test" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc23-" + Guid.NewGuid().ToString("N"));
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

    // ---- Sign-in and accounts ----

    [Fact]
    public async Task Def131_TheHintUnderACardOrCheckLine_IsNotPulledUpOverItsTitle()
    {
        HttpClient client = this.Client();
        string css = await client.GetStringAsync("app.css");
        Assert.DoesNotContain(".authfields .authhint { margin: -12px", css, StringComparison.Ordinal);
        Assert.Contains(".authfields > p.authhint { margin: -12px 0 14px; }", css, StringComparison.Ordinal);
        Assert.Contains(".authcardopt .authhint, .authcheck .authhint { margin: 0; display: block; }", css, StringComparison.Ordinal);
        // The sign-in cards carry their hints as spans inside the card, which the pull-up no longer reaches.
        string page = await client.GetStringAsync("sign-in");
        Assert.Contains("<span class=\"authhint\">Stay signed in</span>", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Def132_ShowMeAround_StartsTheTour_EvenWhenTheFormIsSentByThePageScript()
    {
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string inbox = await client.GetStringAsync("folder/INBOX");
        HttpResponseMessage res = await Post(client, "settings/welcome", Token(inbox), ("tour", "1"));
        Assert.Equal("/dashboard/today?tour=1#tour", res.Headers.Location!.ToString());
        string js = await client.GetStringAsync("app.js");
        Assert.Contains("[?&]tour=1(&|$)", js, StringComparison.Ordinal);
        Assert.Contains("if (askedTour || window.location.hash === \"#tour\") { start(); }", js, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Def138_SigningInAgainWhileSignedIn_AddsTheAccount_AndNeverDropsTheOneOpen()
    {
        HttpClient browser = this.Client();
        await SignInAsync(browser, "arun@sgh.in");
        // The plain sign-in page says who is signed in, and its form adds.
        string page = await browser.GetStringAsync("sign-in");
        Assert.Contains("You are already signed in as arun@sgh.in.", page, StringComparison.Ordinal);
        Assert.Contains("<input type=\"hidden\" name=\"add\" value=\"1\"", page, StringComparison.Ordinal);
        // Even a form without add=1 (an old tab, a bookmark) keeps Arun signed in.
        HttpResponseMessage res = await Post(browser, "auth/login", Token(page), ("address", "meera@sgh.in"), ("password", "correct horse battery"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        string inbox = await browser.GetStringAsync("folder/INBOX");
        Assert.Contains("meera@sgh.in</span>", inbox, StringComparison.Ordinal);
        Assert.Contains("Switch to", inbox, StringComparison.Ordinal);
        Assert.Contains("arun@sgh.in", inbox, StringComparison.Ordinal);
        // Signing in again as the account already open replaces nothing either.
        page = await browser.GetStringAsync("sign-in");
        res = await Post(browser, "auth/login", Token(page), ("address", "arun@sgh.in"), ("password", "correct horse battery"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        inbox = await browser.GetStringAsync("folder/INBOX");
        Assert.Contains("arun@sgh.in</span>", inbox, StringComparison.Ordinal);
        Assert.Contains("meera@sgh.in", inbox, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Def138_ASignedOutVisitor_SeesThePlainSignInPage()
    {
        string page = await this.Client().GetStringAsync("sign-in");
        Assert.DoesNotContain("data-already", page, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"add\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Def133_TheInvitationExpiry_IsWrittenOnAnjalsClock_WithTheZoneNamed()
    {
        // 18:45 UTC on 26 Oct is 00:15 on 27 Oct in India: the mail and the page now both say the 27th.
        var expires = new DateTimeOffset(2026, 10, 26, 18, 45, 0, TimeSpan.Zero);
        Assert.Equal("27 Oct 2026, 00:15 India time", MailboxService.InvitationExpiry(expires, null));
    }

    [Fact]
    public async Task Def126_TheDateOfBirthBox_SaysTheAgeRuleInWords()
    {
        HttpClient client = this.Client();
        string js = await client.GetStringAsync("app.js");
        Assert.Contains("page(function adultDates()", js, StringComparison.Ordinal);
        Assert.Contains("input.setCustomValidity(words)", js, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Def145_BothSignInThrottles_AreWrittenOnTheSecurityPage()
    {
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string security = await client.GetStringAsync("settings/security");
        Assert.Contains("after 5 from one network, that network waits 15 minutes; after 10 in a row from anywhere, signing in with the password is paused for 30 minutes", security, StringComparison.Ordinal);
    }

    [Fact]
    public async Task D172_TheHelpGuide_OpensWithoutSigningIn_WithoutTheAccountOnlyParts()
    {
        HttpClient visitor = this.Client();
        HttpResponseMessage home = await visitor.GetAsync("help");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        string page = await home.Content.ReadAsStringAsync();
        Assert.Contains("How can we help?", page, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/lajna", page, StringComparison.Ordinal);
        string article = await visitor.GetStringAsync("help/two-step");
        Assert.DoesNotContain("Show me in Anjal", article, StringComparison.Ordinal);
        Assert.DoesNotContain("Did this help?", article, StringComparison.Ordinal);
        Assert.Contains("href=\"/sign-in\"", article, StringComparison.Ordinal);
        // Signed in, everything is there as before.
        HttpClient person = this.Client();
        await SignInAsync(person, "arun@sgh.in");
        Assert.Contains("Did this help?", await person.GetStringAsync("help/two-step"), StringComparison.Ordinal);
    }

    // ---- Reading, lists and settings ----

    [Fact]
    public async Task Def118_AnjalsOwnMail_IsNotMarkedAsArrivedWithoutEncryption()
    {
        Assert.Null(await this.svc.SendSystemMailAsync(this.arun, "arun@sgh.in", "Anjal alert: 2 things need attention", "Check the disk."));
        FolderRow? inbox = await this.svc.GetFolderAsync(this.arun.Id, FolderRow.Inbox);
        MessageRow alert = (await this.store.ListMessagesAsync(this.arun.Id, inbox!.Id, 1, 0))[0];
        Assert.Null(alert.TransportEncrypted);
        Assert.False(MailboxService.ShowsUnencryptedWarning(alert, new HashSet<string>()));
        // Mail from outside over plain SMTP still carries the warning.
        MessageRow outside = await this.DeliverAsync("From: Lab <lab@path.test>\r\nSubject: Plain\r\n\r\nx\r\n", "lab@path.test");
        Assert.False(outside.TransportEncrypted);
        Assert.True(MailboxService.ShowsUnencryptedWarning(outside, new HashSet<string>()));
    }

    [Fact]
    public async Task Def150_SlashToday_IsTheTodayPage()
    {
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        HttpResponseMessage res = await client.GetAsync("today");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("<title>Today — Anjal</title>", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Item89_AnAddressThatIsNotAPage_Answers404_WithAPageInAnjalsLook()
    {
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        using var ask = new HttpRequestMessage(HttpMethod.Get, "no/such/page");
        ask.Headers.Accept.ParseAdd("text/html");
        HttpResponseMessage res = await client.SendAsync(ask);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        string page = await res.Content.ReadAsStringAsync();
        Assert.Contains("This page is not in Anjal", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/\"", page, StringComparison.Ordinal);
        // Signed out, the same page offers Sign in.
        using var ask2 = new HttpRequestMessage(HttpMethod.Get, "no/such/page");
        ask2.Headers.Accept.ParseAdd("text/html");
        string signedOut = await (await this.Client().SendAsync(ask2)).Content.ReadAsStringAsync();
        Assert.Contains("This page is not in Anjal", signedOut, StringComparison.Ordinal);
        Assert.Contains("href=\"/sign-in\"", signedOut, StringComparison.Ordinal);
        // The API keeps its bare 404.
        using var api = new HttpRequestMessage(HttpMethod.Get, "api/no-such-thing");
        api.Headers.Accept.ParseAdd("text/html");
        HttpResponseMessage bare = await client.SendAsync(api);
        Assert.Equal(HttpStatusCode.NotFound, bare.StatusCode);
        Assert.DoesNotContain("This page is not in Anjal", await bare.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Item89_RobotsTxt_KeepsTheMailOutOfSearchEngines_AndLetsTheHelpGuideIn()
    {
        HttpResponseMessage res = await this.Client().GetAsync("robots.txt");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/plain", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("User-agent: *\nAllow: /help\nDisallow: /\n", await res.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("help")]
    [InlineData("help/two-step")]
    [InlineData("help/part/security")]
    [InlineData("help/no-such-article")]
    [InlineData("help?q=password")]
    public async Task Item89_EveryHelpPage_HasOneH1(string path)
    {
        HttpClient person = this.Client();
        await SignInAsync(person, "arun@sgh.in");
        Assert.Equal(1, Regex.Count(await person.GetStringAsync(path), "<h1[ >]"));
        Assert.Equal(1, Regex.Count(await this.Client().GetStringAsync(path), "<h1[ >]"));
    }

    [Fact]
    public async Task Def124_TheComponentSheet_RefusesWith401Or403()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await this.Client().GetAsync("dev/components")).StatusCode);
        HttpClient person = this.Client();
        await SignInAsync(person, "meera@sgh.in");
        HttpResponseMessage res = await person.GetAsync("dev/components");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("Only an operator of this Anjal service can open this.", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await person.GetAsync("ops")).StatusCode);
    }

    [Fact]
    public async Task Def125_SecurityTxt_NamesTheAbuseAddress_AndExpiresInAYear()
    {
        HttpResponseMessage res = await this.Client().GetAsync(".well-known/security.txt");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        string text = await res.Content.ReadAsStringAsync();
        Assert.Contains("Contact: mailto:abuse@", text, StringComparison.Ordinal);
        Assert.Contains("Expires: " + DateTime.UtcNow.AddYears(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
        Assert.Equal("Contact: mailto:abuse@anjal.co.in\nExpires: 2027-10-24T00:00:00Z\nPreferred-Languages: en\nCanonical: https://mail.anjal.co.in/.well-known/security.txt\n",
            Program.SecurityTxt("mail.anjal.co.in", new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Def152_EachSnoozeChoice_IsLabelledWithTheDayItComesBack()
    {
        ZonedClock clock = ZonedClock.Default;
        var now = new DateTimeOffset(2026, 10, 23, 4, 0, 0, TimeSpan.Zero);   // Friday 09:30 in India
        string Label(string preset)
        {
            DateTimeOffset at = MailboxService.SnoozeTime(clock, now, preset)!.Value;
            return clock.AheadDay(at, now) + " " + clock.Time(at);
        }
        Assert.Equal("Today 18:00", Label("today"));
        Assert.Equal("Tomorrow 09:00", Label("tomorrow"));
        Assert.Equal("Tomorrow 09:00", Label("weekend"));   // Saturday is tomorrow
        Assert.Equal("Mon 09:00", Label("week"));
        now = new DateTimeOffset(2026, 10, 21, 4, 0, 0, TimeSpan.Zero);   // Wednesday
        Assert.Equal("Sat 09:00", Label("weekend"));
        Assert.Equal("Mon 09:00", Label("week"));
        Assert.Equal("2 Nov 2026", clock.AheadDay(new DateTimeOffset(2026, 11, 2, 4, 0, 0, TimeSpan.Zero), now));
    }

    [Fact]
    public async Task Def153_BlockedSenders_AreNotCountedAsNeverToJunk()
    {
        Assert.Null(await this.svc.AddSenderRuleAsync(this.arun.Id, "spam@nuisance.test", "discard"));
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string page = await client.GetStringAsync("settings/senders");
        Assert.Contains("<span>Never to Junk</span><b>0</b>", page, StringComparison.Ordinal);
        Assert.Contains("<span>Blocked</span><b>1</b>", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Def155_TheAwayForm_SavesWhenOff_WithNoMessage()
    {
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string page = await client.GetStringAsync("settings/away");
        Assert.DoesNotContain("name=\"text\" rows=\"6\" maxlength=\"4000\" required", page, StringComparison.Ordinal);
        HttpResponseMessage off = await Post(client, "settings/away", Token(page), ("subject", ""), ("text", ""));
        Assert.Equal("/settings/away?saved=away", off.Headers.Location!.ToString());
        // On, a message is still needed - the server says so.
        HttpResponseMessage on = await Post(client, "settings/away", Token(page), ("on", "1"), ("subject", ""), ("text", ""));
        Assert.StartsWith("/settings/away?error=", on.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Def158_TheConversationCount_HasAnIconAndASpokenLabel()
    {
        await this.DeliverAsync("From: Lab <lab@path.test>\r\nSubject: Result\r\nMessage-ID: <a1@path.test>\r\n\r\nx\r\n", "lab@path.test");
        await this.DeliverAsync("From: Lab <lab@path.test>\r\nSubject: Re: Result\r\nMessage-ID: <a2@path.test>\r\nIn-Reply-To: <a1@path.test>\r\n\r\nx\r\n", "lab@path.test");
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string inbox = await client.GetStringAsync("folder/INBOX");
        Assert.Contains("class=\"ccount\" role=\"img\"", inbox, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"2 messages in this conversation\"", inbox, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Def159_TheSnoozedMark_SitsAfterTheSubject_InItsOwnColumn()
    {
        HttpClient client = this.Client();
        string css = await client.GetStringAsync("app.css");
        Assert.Contains(".fview:not(.has-open) .mrow .rowmark { grid-column: 6; grid-row: 1;", css, StringComparison.Ordinal);
        Assert.Contains(".fview:not(.has-open) .mrow time { grid-column: 12;", css, StringComparison.Ordinal);
        Assert.Contains("auto auto auto auto auto auto 100px; }", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Def167_EverySystemFolder_SaysWhatItIsFor_WhenEmpty()
    {
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string scheduled = await client.GetStringAsync("folder/Scheduled");
        Assert.Contains("Nothing scheduled.", scheduled, StringComparison.Ordinal);
        Assert.DoesNotContain("Move messages here, or make a rule", scheduled, StringComparison.Ordinal);
        await this.store.EnsureFolderAsync(this.arun.Id, MailboxService.SnoozedFolder);   // made by the first snooze
        Assert.Contains("Nothing snoozed.", await client.GetStringAsync("folder/Snoozed"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Def149e_AFolderNamedInAnotherCase_GoesToItsOwnAddress()
    {
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        HttpResponseMessage res = await client.GetAsync("folder/DRAFTS");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.EndsWith("/folder/Drafts", res.Headers.Location!.ToString(), StringComparison.Ordinal);
        res = await client.GetAsync("folder/inbox?page=0");
        Assert.EndsWith("/folder/INBOX?page=0", res.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Def127_LajnasPage_EmptiesTheBoxAfterAnAnswer()
    {
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string page = await client.GetStringAsync("lajna?q=" + Uri.EscapeDataString("How do I turn on two-step sign-in?"));
        Assert.Contains("<b>You</b> How do I turn on two-step sign-in?", page, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"q\" value=\"How do I", page, StringComparison.Ordinal);
    }

    // ---- Compose and contacts ----

    [Fact]
    public async Task Def120_TheAttachmentLimit_IsExplainedAsFilesAndTheEncodedMessage()
    {
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string mail = await client.GetStringAsync("settings/mail");
        Assert.Contains("up to 18 MB of files per message (25 MB once encoded for sending, the most other mail servers accept).", mail, StringComparison.Ordinal);
        // The words match the cap the server applies.
        Assert.Equal(18L * 1024 * 1024, MailboxService.MaxAttachmentBytes);
    }

    [Fact]
    public async Task Def140_Compose_HasAMovingBarForFilesGoingUp()
    {
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string compose = await client.GetStringAsync("compose");
        Assert.Contains("data-uploadbar role=\"status\" aria-live=\"polite\" hidden", compose, StringComparison.Ordinal);
        string js = await client.GetStringAsync("app.js");
        Assert.Contains("W(\"Sending {size} of files… keep this page open.\")", js, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Def142_AGroup_TakesASavedContactsName_AndSaysWhenANameIsUnknownOrShared()
    {
        Assert.Null(await this.svc.SaveContactAsync(this.arun.Id, new Contact { FirstName = "Ramesh", LastName = "Kumar", Address = "ramesh@lab.test" }));
        Assert.Null(await this.svc.SaveContactAsync(this.arun.Id, new Contact { FirstName = "Priya", LastName = "Nair", Address = "priya@lab.test" }));
        Assert.Null(await this.svc.SaveContactAsync(this.arun.Id, new Contact { FirstName = "Priya", LastName = "Rao", Address = "priya.rao@lab.test" }));
        var group = new ContactGroup { Name = "Lab", Addresses = { "ramesh", "Priya Nair", "anil@lab.test" } };
        Assert.Null(await this.svc.SaveGroupAsync(this.arun.Id, group));
        ContactGroup saved = (await this.svc.ListGroupsAsync(this.arun.Id)).Single();
        Assert.Equal(LabGroup, saved.Addresses);
        Assert.Equal("More than one contact is called Priya. Type the address instead.", await this.svc.SaveGroupAsync(this.arun.Id, new ContactGroup { Name = "Two", Addresses = { "Priya" } }));
        Assert.Equal("No saved contact is called Suresh. Type the address, or save them as a contact first.", await this.svc.SaveGroupAsync(this.arun.Id, new ContactGroup { Name = "Three", Addresses = { "Suresh" } }));
        // The box offers names as they are typed.
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string page = await client.GetStringAsync("contacts?tab=groups&new=1");
        Assert.Contains("data-group-members", page, StringComparison.Ordinal);
        Assert.Contains("data-group-suggest role=\"listbox\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Def143And144_Contacts_HasItsOwnFileButton_AndAddButtonsThatSayWhatTheyAdd()
    {
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string people = await client.GetStringAsync("contacts");
        Assert.Contains("<span class=\"filename\" id=\"importname\">No file chosen</span>", people, StringComparison.Ordinal);
        Assert.Contains("data-filename-for=\"importname\"", people, StringComparison.Ordinal);
        Assert.Contains(">Add person</a>", people, StringComparison.Ordinal);
        Assert.Contains(">New group</a>", await client.GetStringAsync("contacts?tab=groups"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Def166_Compose_SaysWhereTheAddressesGo_TheFirstTime()
    {
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string compose = await client.GetStringAsync("compose");
        Assert.Contains("data-compose-first hidden", compose, StringComparison.Ordinal);
        Assert.Contains("The addresses go in the envelope on the right: To, Cc and Bcc.", compose, StringComparison.Ordinal);
        // Not on a reply or a forward: the envelope is filled already, and the letter needs the room.
        MessageRow m = await this.DeliverAsync("From: Lab <lab@path.test>\r\nSubject: Result\r\n\r\nx\r\n", "lab@path.test");
        Assert.DoesNotContain("data-compose-first", await client.GetStringAsync($"compose?replyall={m.Id}"), StringComparison.Ordinal);
        Assert.DoesNotContain("data-compose-first", await client.GetStringAsync($"compose?forward={m.Id}"), StringComparison.Ordinal);
        string css = await client.GetStringAsync("app.css");
        Assert.Contains(".cform .csubject:focus { outline: none; border-bottom-color: var(--brand);", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Def130_WithNicknamesOff_TheNameOnMails_IsLockedAndRefused()
    {
        await this.svc.SaveMailPolicyAsync(this.sgh.Id, new OrgMailPolicy { Nicknames = false });
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string profile = await client.GetStringAsync("settings/profile");
        Assert.Matches("id=\"displayName\"[^>]*disabled", profile);
        HttpResponseMessage res = await Post(client, "settings/name", Token(profile), ("displayName", "Dr Arun"));
        Assert.StartsWith("/settings/profile?error=", res.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    // ---- Organisation console, rules and operator ----

    [Fact]
    public async Task Def122_ADomain_IsCancelledWhilePending_AndRemovedOnlyWhenNothingLivesAtIt()
    {
        Assert.Null(await this.svc.SetAdminAsync(this.sgh, this.arun.Id, true));
        HttpClient admin = this.Client();
        await SignInAsync(admin, "arun@sgh.in");
        string page = await admin.GetStringAsync("org/domains");
        // Pending: cancelled with one press.
        Assert.Equal(HttpStatusCode.Redirect, (await Post(admin, "org/domains/add", Token(page), ("domain", "test.sgh.in"))).StatusCode);
        page = await admin.GetStringAsync("org/domains?domain=test.sgh.in");
        Assert.Contains("Cancel this domain", page, StringComparison.Ordinal);
        HttpResponseMessage res = await Post(admin, "org/domains/remove", Token(page), ("domain", "test.sgh.in"));
        Assert.Equal("/org/domains?saved=domain-removed", res.Headers.Location!.ToString());
        Assert.DoesNotContain("test.sgh.in", await admin.GetStringAsync("org/domains"), StringComparison.Ordinal);
        // Proved, with mailboxes at it: refused, and it stays.
        Assert.Contains("Still in use: 2 mailbox(es)", await this.svc.RemoveTenantDomainAsync(this.sgh, "sgh.in"), StringComparison.Ordinal);
        Assert.NotNull(await this.store.GetTenantDomainAsync("sgh.in"));
        // Proved, empty: removed with its DKIM key, once the domain is typed.
        Assert.Null(await this.svc.AddTenantDomainAsync(this.sgh, "clinic.sgh-group.in"));
        page = await admin.GetStringAsync("org/domains?domain=clinic.sgh-group.in");
        Assert.Contains("Remove this domain", page, StringComparison.Ordinal);
        res = await Post(admin, "org/domains/remove", Token(page), ("domain", "clinic.sgh-group.in"), ("value", "wrong.in"));
        Assert.Contains("&e=", res.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.NotNull(await this.store.GetTenantDomainAsync("clinic.sgh-group.in"));
        res = await Post(admin, "org/domains/remove", Token(page), ("domain", "clinic.sgh-group.in"), ("value", "clinic.sgh-group.in"));
        Assert.Equal("/org/domains?saved=domain-removed", res.Headers.Location!.ToString());
        Assert.Null(await this.store.GetTenantDomainAsync("clinic.sgh-group.in"));
        Assert.Null(await this.store.GetDkimKeyAsync("clinic.sgh-group.in"));
    }

    [Fact]
    public async Task Def163_ASharedMailbox_CanBeDeleted_ByTypingItsAddress()
    {
        Assert.Null(await this.svc.SetAdminAsync(this.sgh, this.arun.Id, true));
        HttpClient admin = this.Client();
        await SignInAsync(admin, "arun@sgh.in");
        string page = await admin.GetStringAsync("org/shared?new=1");
        Assert.Equal(HttpStatusCode.Redirect, (await Post(admin, "org/shared/new", Token(page), ("local", "qa-shared"), ("domain", "sgh.in"), ("name", "QA"))).StatusCode);
        MailboxRow box = (await this.store.ListMailboxesAsync(this.sgh.Id)).Single(m => m.LocalPart == "qa-shared");
        page = await admin.GetStringAsync("org/shared?sel=" + box.Id);
        Assert.Contains("Delete this shared mailbox", page, StringComparison.Ordinal);
        HttpResponseMessage res = await Post(admin, "org/shared/" + box.Id + "/delete", Token(page), ("value", "qa-shared@sgh.in"));
        Assert.Equal("/org/shared?saved=shared-removed", res.Headers.Location!.ToString());
        Assert.Null(await this.store.GetMailboxByIdAsync(box.Id));
        Assert.DoesNotContain(box.Id, (await this.svc.SharedOfTenantAsync(this.sgh.Id)).Keys);
        // A person's own mailbox is not a shared one.
        Assert.Equal("That is not one of this organisation's shared mailboxes.", await this.svc.DeleteSharedMailboxAsync(this.sgh, this.meera.Id, this.root));
    }

    [Fact]
    public async Task Def162_AnApplication_IsRetired_ThenRemoved_AndItsKeysStopWorking()
    {
        Assert.Null(await this.svc.SetAdminAsync(this.sgh, this.arun.Id, true));
        (string? error, string? appId, string? user, string? _) = await this.svc.AddAppAsync(this.sgh, "QA Test App", "Testing", "qa@sgh.in", string.Empty);
        Assert.Null(error);
        Assert.NotNull(await this.store.GetSmtpUserAsync(user!));
        HttpClient admin = this.Client();
        await SignInAsync(admin, "arun@sgh.in");
        string page = await admin.GetStringAsync("org/apps?sel=" + appId);
        Assert.Contains("asidebtn\">Retire this application</summary>", page, StringComparison.Ordinal);
        Assert.DoesNotContain("asidebtn\">Remove from the list</summary>", page, StringComparison.Ordinal);   // it still has a key
        Assert.Equal("This application still has keys. Retire it first, so its keys stop working.", await this.svc.RemoveAppAsync(this.sgh, appId!));
        HttpResponseMessage res = await Post(admin, "org/apps/" + appId + "/retire", Token(page), ("value", "QA Test App"));
        Assert.EndsWith("&saved=app-retired", res.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Null(await this.store.GetSmtpUserAsync(user!));
        OrgApp retired = (await this.svc.AppsOfAsync(this.sgh.Id)).Single();
        Assert.NotNull(retired.Retired);
        Assert.Null(await this.svc.NewKeyAsync(this.sgh, appId!, rotate: false));
        page = await admin.GetStringAsync("org/apps?sel=" + appId);
        Assert.Contains("asidebtn\">Remove from the list</summary>", page, StringComparison.Ordinal);
        res = await Post(admin, "org/apps/" + appId + "/remove", Token(page), ("value", "QA Test App"));
        Assert.Equal("/org/apps?saved=app-removed", res.Headers.Location!.ToString());
        Assert.Empty(await this.svc.AppsOfAsync(this.sgh.Id));
    }

    [Fact]
    public async Task Def157_TheNewKey_IsCalledKey_AndSaysItIsShownOnce()
    {
        Assert.Null(await this.svc.SetAdminAsync(this.sgh, this.arun.Id, true));
        HttpClient admin = this.Client();
        await SignInAsync(admin, "arun@sgh.in");
        string page = await admin.GetStringAsync("org/apps?new=1");
        HttpResponseMessage res = await Post(admin, "org/apps/new", Token(page), ("name", "HIS"), ("sendsAs", "his@sgh.in"));
        string shown = await admin.GetStringAsync(res.Headers.Location!.ToString().TrimStart('/'));
        Assert.Contains("Copy the new key now: it is shown once", shown, StringComparison.Ordinal);
        Assert.Contains("<dt>Key</dt>", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("<dt>Password</dt>", shown, StringComparison.Ordinal);
        Assert.Contains("Keys in use", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Def123_ThePeopleFooter_CountsInvitedPeople()
    {
        Assert.Null(await this.svc.SetAdminAsync(this.sgh, this.arun.Id, true));
        HttpClient admin = this.Client();
        await SignInAsync(admin, "arun@sgh.in");
        string page = await admin.GetStringAsync("org/people?add=1&mode=invite");
        Assert.Equal(HttpStatusCode.Redirect, (await Post(admin, "org/people/invite", Token(page), ("first", "Demo"), ("last", "Qa"), ("local", "demo.qa"), ("domain", "sgh.in"), ("personal", "demo@gmail.test"))).StatusCode);
        Assert.Contains("Showing 3 of 3.", await admin.GetStringAsync("org/people"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Def164_EveryConsoleSection_HasAnIconTheLibraryDraws()
    {
        Assert.Null(await this.svc.SetAdminAsync(this.sgh, this.arun.Id, true));
        HttpClient admin = this.Client();
        await SignInAsync(admin, "arun@sgh.in");
        string page = await admin.GetStringAsync("org/rules");
        MatchCollection links = Regex.Matches(page, "<a href=\"/org/[a-z]+\" class=\"setnav-item[^\"]*\"[^>]*>(.*?)</a>", RegexOptions.Singleline);
        Assert.True(links.Count >= 12, "nav links found: " + links.Count);
        Assert.All(links, m => Assert.Contains("<svg", m.Groups[1].Value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Def121_TheDnsTemplate_IsAtReject_AndTheDailyCheckIsShown()
    {
        IReadOnlyList<DnsRecordCheck> records = await this.svc.DomainRecordsAsync("sgh.in", null);
        Assert.Equal("v=DMARC1; p=reject; adkim=s; aspf=s; sp=reject; rua=mailto:dmarc-reports@sgh.in", records.Single(r => r.Kind == "DMARC").Value);
        Assert.Equal(1, await this.svc.CheckDnsDailyAsync(DateTimeOffset.UtcNow, name => Task.FromResult<IReadOnlyList<string>?>(name == "MX:sgh.in" ? OurHost : Array.Empty<string>())));
        Assert.Null(await this.svc.SetAdminAsync(this.sgh, this.arun.Id, true));
        HttpClient admin = this.Client();
        await SignInAsync(admin, "arun@sgh.in");
        string page = await admin.GetStringAsync("org/domains?domain=sgh.in");
        Assert.Contains("Checked by the daily check,", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Not checked yet.", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task D171_TheWebhooksTab_SaysWebhooksAreTheOperatorsForNow()
    {
        Assert.Null(await this.svc.SetAdminAsync(this.sgh, this.arun.Id, true));
        HttpClient admin = this.Client();
        await SignInAsync(admin, "arun@sgh.in");
        Assert.Contains("Webhooks are set up by the Anjal operator for now.", await admin.GetStringAsync("org/apps?tab=webhooks"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task D176_TryingARule_SavesNothing_AndApplyingNeedsTheBoxTicked()
    {
        await this.svc.SaveMailPolicyAsync(this.sgh.Id, new OrgMailPolicy { ForwardingOutside = true });
        await this.DeliverAsync("From: Lab <lab@path.test>\r\nSubject: Old result\r\n\r\nx\r\n", "lab@path.test");
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string page = await client.GetStringAsync("settings/rules?rule=new");
        (string, string)[] rule = { ("id", ""), ("name", "Lab"), ("field", "from"), ("op", "contains"), ("value", "lab@"), ("forwardTo", "arun.home@gmail.test"), ("enabled", "1") };
        // Try: nothing saved, nothing sent.
        HttpResponseMessage res = await Post(client, "settings/rules/save", Token(page), rule.Append(("trial", "1")).ToArray());
        Assert.Equal("/settings/rules?rule=new&trial=1", res.Headers.Location!.ToString());
        Assert.Empty(await this.svc.ListRulesAsync(this.arun.Id));
        Assert.Empty(await this.store.ListOutboundForSenderAsync("arun@sgh.in"));
        string tried = await client.GetStringAsync("settings/rules?rule=new&trial=1");
        Assert.Contains("nothing was saved, moved or sent", tried, StringComparison.Ordinal);
        Assert.Contains("value=\"Lab\"", tried, StringComparison.Ordinal);
        Assert.Contains("Save and apply to older mail now", tried, StringComparison.Ordinal);
        Assert.Contains("<span>I understand this saves the rule and acts on 1 older messages now, and sends a copy of each to arun.home@gmail.test.</span>", tried, StringComparison.Ordinal);
        // Apply without the box: nothing done.
        res = await Post(client, "settings/rules/save", Token(tried), rule.Append(("applyNow", "1")).ToArray());
        Assert.Contains("problem=", res.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Empty(await this.svc.ListRulesAsync(this.arun.Id));
        Assert.Empty(await this.store.ListOutboundForSenderAsync("arun@sgh.in"));
        // With the box: saved and applied.
        res = await Post(client, "settings/rules/save", Token(tried), rule.Append(("applyNow", "1")).Append(("applyConfirm", "1")).ToArray());
        Assert.Contains("notice=applied&n=1", res.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Single(await this.svc.ListRulesAsync(this.arun.Id));
        Assert.Single(await this.store.ListOutboundForSenderAsync("arun@sgh.in"));
    }

    [Fact]
    public async Task Def151_TheOperatorConsole_SaysClamAvInWords()
    {
        string? before = Environment.GetEnvironmentVariable("ANJAL_OPERATORS");
        try
        {
            Environment.SetEnvironmentVariable("ANJAL_OPERATORS", "arun@sgh.in");
            HttpClient op = this.Client();
            await SignInAsync(op, "arun@sgh.in");
            string page = await op.GetStringAsync("ops/health");
            Assert.DoesNotContain("ANJAL_CLAMAV", page, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANJAL_OPERATORS", before);
        }
    }

    [Fact]
    public async Task Def139_TheActivityLogBadge_KeepsToTheRightEdge()
    {
        string css = await this.Client().GetStringAsync("app.css");
        Assert.Contains(".logtable tbody td:nth-child(6) { grid-area: result; justify-self: end; }", css, StringComparison.Ordinal);
    }

    // ---- Phone and tablet ----

    [Fact]
    public async Task Def146_ThePhoneStrip_OpensTheEnvelopeOnThisPage_AndBareHashLinksStay()
    {
        MessageRow m = await this.DeliverAsync("From: Lab <lab@path.test>\r\nSubject: Result\r\n\r\nx\r\n", "lab@path.test");
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string page = await client.GetStringAsync("folder/INBOX?open=" + m.Id);
        Assert.Contains("class=\"phonesummary\" href=\"/folder/INBOX?open=" + m.Id + "#envelope\"", page, StringComparison.Ordinal);
        string js = await client.GetStringAsync("app.js");
        Assert.Contains("if (window.location.hash !== raw) { window.location.hash = raw.slice(1); }", js, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchWithAMessageOpen_NeverFails_WhileTheResultsLoad()
    {
        // The rc.23 screen check found a 500 on /search?q=...&open=: the page renders twice, and the
        // reader's second start cleared the message the first was still reading.
        MessageRow m = await this.DeliverAsync("From: Lab <lab@path.test>\r\nSubject: Lab result\r\n\r\nlab x\r\n", "lab@path.test");
        for (int i = 0; i < 4; i++)
        {
            await this.DeliverAsync($"From: Lab <lab@path.test>\r\nSubject: Lab note {i}\r\n\r\nlab {i}\r\n", "lab@path.test");
        }
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        foreach (string q in SearchWords)
        {
            for (int i = 0; i < 5; i++)
            {
                HttpResponseMessage res = await client.GetAsync(new Uri($"search?q={q}&scope=all&from=INBOX&page=1&open={m.Id}", UriKind.Relative));
                Assert.Equal(HttpStatusCode.OK, res.StatusCode);
                Assert.Contains("Lab result", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Def147And148_TheReaderStacksWhenNarrow_AndLandscapePhonesGetThePhoneLayout()
    {
        string css = await this.Client().GetStringAsync("app.css");
        Assert.Contains(".fview .readcol { container: reader / inline-size; }", css, StringComparison.Ordinal);
        Assert.Contains("@container reader (max-width: 600px)", css, StringComparison.Ordinal);
        Assert.DoesNotContain("@media (max-width: 720px) {", css, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 720px), (max-height: 480px) and (max-width: 950px) {", css, StringComparison.Ordinal);
        Assert.Contains("@media (min-width: 721px) and (min-height: 481px), (min-width: 951px) {", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Def149a_TheOperator_FindsTheAnjalConsoleInThePhoneMenu()
    {
        string? before = Environment.GetEnvironmentVariable("ANJAL_OPERATORS");
        try
        {
            Environment.SetEnvironmentVariable("ANJAL_OPERATORS", "arun@sgh.in");
            HttpClient op = this.Client();
            await SignInAsync(op, "arun@sgh.in");
            string page = await op.GetStringAsync("folder/INBOX");
            int sheet = page.IndexOf("id=\"folders\" class=\"phonesheet\"", StringComparison.Ordinal);
            Assert.True(sheet > 0);
            Assert.Contains("<a class=\"pfolder\" href=\"/ops\">", page[sheet..], StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANJAL_OPERATORS", before);
        }
    }

    [Fact]
    public async Task Def149bAnd149d_ClosingComposeCountsATypedAddress_AndThePeopleColumnStays()
    {
        HttpClient client = this.Client();
        string js = await client.GetStringAsync("app.js");
        Assert.Contains("$$(\"[data-recips] input:not([type='hidden'])\", form).forEach(function (i) { parts.push(i.value.trim()); });", js, StringComparison.Ordinal);
        string css = await client.GetStringAsync("app.css");
        Assert.Contains(".peopletable th:first-child, .peopletable td:first-child { position: sticky; left: 0;", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Def160_MailSettings_OffersATestNotification()
    {
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        Assert.Contains("data-notify-test", await client.GetStringAsync("settings/mail"), StringComparison.Ordinal);
        Assert.Contains("icon: anjalIcon()", await client.GetStringAsync("app.js"), StringComparison.Ordinal);
    }

    // ---- helpers ----

    private async Task<MailboxRow> Person(string local, string name) =>
        await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = this.sgh.Id,
            LocalPart = local,
            Domain = "sgh.in",
            DisplayName = name,
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        });

    private async Task<MessageRow> DeliverAsync(string raw, string envelopeFrom, string[]? to = null)
    {
        DeliveryResult result = await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = envelopeFrom,
            EnvelopeTo = to ?? ArunOnly,
            RawBytes = Encoding.UTF8.GetBytes(raw),
        });
        Assert.Equal(DeliveryOutcome.Accepted, result.Outcome);
        FolderRow? inbox = await this.svc.GetFolderAsync(this.arun.Id, FolderRow.Inbox);
        return (await this.store.ListMessagesAsync(this.arun.Id, inbox!.Id, 1, 0))[0];
    }

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
        return await client.PostAsync(path, content);
    }
}
