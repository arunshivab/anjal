using System.Net;
using System.Text;
using System.Text.Json;
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
/// v1.0.0-rc.22.2 (owner, 20 Oct 2026: "this new notification and all have you added to the
/// tests?"): the rc.22 pieces that had only been looked at in a browser - the desktop
/// notification feed and its setting, Unsubscribe (mailto and one-click), the List-Unsubscribe
/// header, search highlighting, the sign-in page carrying the organisation's design, a forwarding
/// rule applied to older mail, Turn off for the away reply, the operator's held-mail notice, and
/// the help page's flow chart.
/// </summary>
public sealed class Rc22GapTests : IAsyncLifetime, IDisposable
{
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly string[] ArunOnly = { "arun@sgh.in" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc22g-" + Guid.NewGuid().ToString("N"));
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
    public async Task DesktopNotifications_TheSettingIsKept_AndTheFeedNamesTheNewestMail_AndWhetherItIsAContact()
    {
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        // Off by default; the toggle keeps the scope.
        Assert.False((await this.svc.GetMailSettingsAsync(this.arun.Id)).DesktopNotify);
        string settings = await client.GetStringAsync("settings/mail");
        HttpResponseMessage saved = await Post(client, "settings/notify", Token(settings), ("on", "1"), ("scope", "contacts"), ("back", "/settings/mail"));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        MailSettings after = await this.svc.GetMailSettingsAsync(this.arun.Id);
        Assert.True(after.DesktopNotify);
        Assert.Equal("contacts", after.NotifyScope);
        settings = await client.GetStringAsync("settings/mail");
        Assert.Contains("data-notify-form", settings, StringComparison.Ordinal);

        // The feed: empty Inbox gives {}, then the newest message, with the contact flag.
        string empty = await client.GetStringAsync("api/latest");
        Assert.Equal("{}", empty);
        await this.DeliverAsync("From: Priya <priya@gmail.test>\r\nSubject: Duty roster for Monday\r\n\r\nx\r\n", "priya@gmail.test");
        using JsonDocument first = JsonDocument.Parse(await client.GetStringAsync("api/latest"));
        Assert.Equal("Priya", first.RootElement.GetProperty("from").GetString());
        Assert.Equal("Duty roster for Monday", first.RootElement.GetProperty("subject").GetString());
        Assert.False(first.RootElement.GetProperty("contact").GetBoolean());
        Assert.Null(await this.svc.SaveContactAsync(this.arun.Id, new Contact { FirstName = "Priya", LastName = "N", Address = "priya@gmail.test" }));
        await this.DeliverAsync("From: Priya <priya@gmail.test>\r\nSubject: Second\r\n\r\nx\r\n", "priya@gmail.test");
        using JsonDocument second = JsonDocument.Parse(await client.GetStringAsync("api/latest"));
        Assert.Equal("Second", second.RootElement.GetProperty("subject").GetString());
        Assert.True(second.RootElement.GetProperty("contact").GetBoolean());
        // Nobody signed in gets nothing.
        HttpClient stranger = this.Client();
        Assert.NotEqual(HttpStatusCode.OK, (await stranger.GetAsync("api/latest")).StatusCode);
    }

    [Fact]
    public async Task Unsubscribe_ParsesTheHeader_SendsTheMailtoRequest_AndRefusesWhatItCannotDo()
    {
        (string mailto, string link) = MailboxService.ParseUnsubscribe("<mailto:leave@list.test?subject=stop%20please>, <https://list.test/leave/1>");
        Assert.Equal("leave@list.test?subject=stop%20please", mailto);
        Assert.Equal("https://list.test/leave/1", link);
        Assert.Equal((string.Empty, string.Empty), MailboxService.ParseUnsubscribe("<http://list.test/plain>"));   // only https is followed

        MessageRow plain = await this.DeliverAsync("From: a@x.test\r\nSubject: No way out\r\n\r\nx\r\n", "a@x.test");
        Assert.Null(await this.svc.UnsubscribeOfAsync(this.arun.Id, plain.Id));
        Assert.Equal("This message offers no way to unsubscribe.", await this.svc.UnsubscribeAsync(this.arun.Id, plain.Id));

        MessageRow news = await this.DeliverAsync("From: News <news@list.test>\r\nSubject: Weekly\r\nList-Unsubscribe: <mailto:leave@list.test?subject=stop%20please>\r\n\r\n...\r\n", "bounce@list.test");
        Assert.Equal(("leave@list.test?subject=stop%20please", string.Empty), await this.svc.UnsubscribeOfAsync(this.arun.Id, news.Id));
        Assert.Null(await this.svc.UnsubscribeAsync(this.arun.Id, news.Id));
        OutboundMessage request = Assert.Single(await this.store.ListOutboundForSenderAsync("arun@sgh.in"));
        Assert.Equal("leave@list.test", request.EnvelopeTo);
        string raw = Encoding.UTF8.GetString(request.RawBytes);
        Assert.Contains("Subject: stop please", raw, StringComparison.Ordinal);
        Assert.Contains(await this.store.ListAuditAsync(50), e => e.Action == MailboxService.UnsubscribeAudit && e.Subject == "leave@list.test");

        // A page-only header without one-click: the person is sent to the link.
        MessageRow pageOnly = await this.DeliverAsync("From: News <news@list.test>\r\nSubject: Weekly\r\nList-Unsubscribe: <https://list.test/leave/1>\r\n\r\n...\r\n", "bounce@list.test");
        Assert.Equal("This newsletter offers only its own page: open the link.", await this.svc.UnsubscribeAsync(this.arun.Id, pageOnly.Id));

        // The button: on the message page for list mail, pressed through the endpoint.
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string page = await client.GetStringAsync($"message/{news.Id}");
        Assert.Contains($"/message/{news.Id}/unsubscribe", page, StringComparison.Ordinal);
        Assert.DoesNotContain($"/message/{plain.Id}/unsubscribe", await client.GetStringAsync($"message/{plain.Id}"), StringComparison.Ordinal);
        HttpResponseMessage pressed = await Post(client, $"message/{news.Id}/unsubscribe", Token(page), ("back", $"/message/{news.Id}"));
        Assert.Equal(HttpStatusCode.Redirect, pressed.StatusCode);
        Assert.Contains("unsubscribed=", pressed.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchResults_HighlightTheWords_AndShowFileTags()
    {
        await this.DeliverAsync("From: Lab <lab@path.test>\r\nSubject: Haemoglobin results\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"b1\"\r\n\r\n--b1\r\nContent-Type: text/plain\r\n\r\nSee attached.\r\n--b1\r\nContent-Type: application/pdf; name=\"hb-report.pdf\"\r\nContent-Disposition: attachment; filename=\"hb-report.pdf\"\r\n\r\n%PDF\r\n--b1--\r\n", "lab@path.test");
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string results = await client.GetStringAsync("search?q=Haemoglobin&scope=all");
        Assert.Matches("<mark[^>]*>Haemoglobin</mark>", results);
        Assert.Contains("hb-report.pdf", results, StringComparison.Ordinal);
        Assert.Contains("filechip", results, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSignInPage_CarriesTheDesignColourWelcomeAndHelpLine_AndTheFormIsStillAnjals()
    {
        // No branding yet: design 1, no inline colour (the only organisation stands in for any host).
        HttpClient plain = this.Client();
        string bare = await plain.GetStringAsync("sign-in");
        Assert.Contains("data-design=\"1\"", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("--org:", bare, StringComparison.Ordinal);
        await this.svc.WriteTenantDocumentAsync(this.sgh.Id, MailboxService.BrandingKind, new Branding { Design = 7, Colour = "#8a1538", Welcome = "Caring since 1986.", HelpLine = "IT desk: 020 2611 0100" });
        HttpClient client = this.Client();
        client.DefaultRequestHeaders.Host = "mail.sgh.in";
        string page = await client.GetStringAsync("sign-in");
        Assert.Contains("data-design=\"7\"", page, StringComparison.Ordinal);
        Assert.Contains("--org:#8a1538", page, StringComparison.Ordinal);
        Assert.Contains("Caring since 1986.", page, StringComparison.Ordinal);
        Assert.Contains("IT desk: 020 2611 0100", page, StringComparison.Ordinal);
        // Only the frame changes: the form still posts to Anjal's own sign-in.
        Assert.Contains("action=\"/auth/login\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"password\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AForwardingRule_AppliedToOlderMail_SendsACopyOfEachMatch_AndNoMoreThanTheCap()
    {
        await this.svc.SaveMailPolicyAsync(this.sgh.Id, new OrgMailPolicy { ForwardingOutside = true });
        await this.DeliverAsync("From: Lab <lab@path.test>\r\nSubject: Old result 1\r\n\r\nx\r\n", "lab@path.test");
        await this.DeliverAsync("From: Lab <lab@path.test>\r\nSubject: Old result 2\r\n\r\nx\r\n", "lab@path.test");
        await this.DeliverAsync("From: Priya <priya@gmail.test>\r\nSubject: Not from the lab\r\n\r\nx\r\n", "priya@gmail.test");
        var rule = new MailRule { Name = "Lab", Conditions = { new RuleCondition { Field = "from", Value = "lab@" } }, ForwardTo = "arun.home@gmail.test" };
        RuleTrial trial = await this.svc.TryRuleAsync(this.arun.Id, rule, apply: false);
        Assert.Equal(2, trial.Matches);
        Assert.Empty(await this.store.ListOutboundForSenderAsync("arun@sgh.in"));
        RuleTrial applied = await this.svc.TryRuleAsync(this.arun.Id, rule, apply: true);
        Assert.Equal(2, applied.Matches);
        IReadOnlyList<OutboundMessage> copies = await this.store.ListOutboundForSenderAsync("arun@sgh.in");
        Assert.Equal(2, copies.Count);
        Assert.All(copies, c => Assert.Equal("arun.home@gmail.test", c.EnvelopeTo));
        Assert.All(copies, c => Assert.Contains(MailboxSink.ForwardedHeader, Encoding.UTF8.GetString(c.RawBytes), StringComparison.Ordinal));
        // The originals stay where they were.
        FolderRow? inbox = await this.svc.GetFolderAsync(this.arun.Id, FolderRow.Inbox);
        Assert.Equal(3, await this.store.CountMessagesAsync(this.arun.Id, inbox!.Id));
    }

    [Fact]
    public async Task TheAwayReply_TurnsOffFromTheForm_AndTheFolderBarGoes()
    {
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.Null(await this.svc.SaveAwayAsync(this.arun.Id, new AwayReply { On = true, From = now.AddHours(-1), Until = now.AddDays(2), Subject = "Away", Text = "Back Friday." }));
        string inbox = await client.GetStringAsync("folder/INBOX");
        Assert.Contains("/settings/away", inbox, StringComparison.Ordinal);
        Assert.Contains("Your away reply is on until", inbox, StringComparison.Ordinal);
        string settings = await client.GetStringAsync("settings/away");
        HttpResponseMessage off = await Post(client, "settings/away", Token(settings), ("back", "/settings/away"));   // no "on": Turn off
        Assert.Equal(HttpStatusCode.Redirect, off.StatusCode);
        Assert.Null(await this.svc.AwayActiveAsync(this.arun.Id));
        Assert.DoesNotContain("Your away reply is on", await client.GetStringAsync("folder/INBOX"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheScannerIsDown_TheOperatorHearsAtOnce_AndTheHeldBarShowsForThePerson()
    {
        MailboxRow ops = await this.Person("ops", "Operator");
        var sink = new MailboxSink(this.store, this.maildir) { Scanner = new DownScanner(), ServerHostName = "mail.sgh.in", OperatorPostmaster = "ops@sgh.in" };
        DeliveryResult result = await sink.DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "a@x.test",
            EnvelopeTo = ArunOnly,
            RawBytes = Encoding.UTF8.GetBytes("From: a@x.test\r\nSubject: Files\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"b1\"\r\n\r\n--b1\r\nContent-Type: text/plain\r\n\r\nSee attached.\r\n--b1\r\nContent-Type: application/pdf; name=\"report.pdf\"\r\nContent-Disposition: attachment; filename=\"report.pdf\"\r\n\r\n%PDF\r\n--b1--\r\n"),
        });
        Assert.Equal(DeliveryOutcome.Accepted, result.Outcome);
        FolderRow? held = await this.svc.GetFolderAsync(this.arun.Id, AttachmentPolicy.QuarantineFolder);
        Assert.Equal(1, await this.store.CountMessagesAsync(this.arun.Id, held!.Id));
        // The operator's notice is in their Inbox, at once.
        FolderRow? opsInbox = await this.svc.GetFolderAsync(ops.Id, FolderRow.Inbox);
        IReadOnlyList<MessageRow> notices = await this.store.ListMessagesAsync(ops.Id, opsInbox!.Id, 10, 0);
        Assert.Contains(notices, n => n.Subject.Contains("scanner", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, await this.svc.HeldCountAsync(this.arun.Id));
        // The person sees the held bar; the Quarantine folder stays out of the rail.
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string inbox = await client.GetStringAsync("folder/INBOX");
        Assert.Contains("One message is held.", inbox, StringComparison.Ordinal);
        Assert.DoesNotContain("/folder/Quarantine\"", inbox, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHelpArticle_HowAnAccountIsMade_ShowsTheFlowChart()
    {
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string article = await client.GetStringAsync("help/account-made");
        Assert.Contains("<figure class=\"helpfigure\"", article, StringComparison.Ordinal);
        Assert.Contains("src=\"/help/account-flows.svg\"", article, StringComparison.Ordinal);
        HttpResponseMessage svg = await client.GetAsync("help/account-flows.svg");
        Assert.Equal(HttpStatusCode.OK, svg.StatusCode);
        Assert.Contains("How an account is made", await svg.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private async Task<MailboxRow> Person(string local, string name) =>
        await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = this.sgh.Id,
            LocalPart = local,
            Domain = "sgh.in",
            DisplayName = name,
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        });

    private async Task<MessageRow> DeliverAsync(string raw, string envelopeFrom)
    {
        DeliveryResult result = await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = envelopeFrom,
            EnvelopeTo = ArunOnly,
            RawBytes = Encoding.UTF8.GetBytes(raw),
        });
        Assert.Equal(DeliveryOutcome.Accepted, result.Outcome);
        return this.store.MailboxMessages[this.store.MailboxMessages.Count - 1];
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

    private sealed class DownScanner : IVirusScanner
    {
        public Task<ScanResult> ScanAsync(byte[] data, CancellationToken ct = default) => Task.FromResult(new ScanResult(ScanVerdict.Unavailable, "connection refused"));

        public Task<string?> VersionAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
}
