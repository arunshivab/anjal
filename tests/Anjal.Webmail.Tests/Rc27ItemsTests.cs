using System.Net;
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
/// rc.27 (item 72 and the carried items, DES-17 §5-§7): the patient-warning choice in the activity
/// log without the mail's words, empty drafts removed after 30 days but never under a hold, the
/// console's four charts and the screen-reader check, spam per organisation in the Monday summary,
/// the shortcut letters from the one map, and reduced motion everywhere.
/// </summary>
[Collection("Operators")]
public sealed class Rc27ItemsTests : IAsyncLifetime, IDisposable
{
    private static readonly string[] ThreeOutside = { "text", "priya@gmail.test, lab@city-lab.test, ravi@gmail.test", "phone number", "phone number", "record number", "a file named Ramesh_MRN-10443.pdf" };
    private static readonly string[] OneOutside = { "text", "priya@gmail.test", "phone number" };
    private static readonly string[] NoAddress = { "text" };
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc27i-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly List<HttpClient> clients = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private string baseAddress = string.Empty;
    private TenantRow tenant = new();
    private MailboxRow arun = new();
    private MailboxRow ravi = new();

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
        this.tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "Imagiqa", PostmasterMailbox = "arun@anjal.co.in" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.tenant.Id, Domain = "anjal.co.in" });
        this.arun = await this.Person("arun", "Arun Shiva B");
        this.ravi = await this.Person("ravi", "Ravi Kumar");
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

    private MailboxService Svc => this.app.Services.GetRequiredService<MailboxService>();

    [Fact]
    public async Task PatientWarning_IsLogged_WithWhatWasNoticedAndTheDomains_NeverTheWords()
    {
        // rc.27 (item 72.4): what Anjal noticed (kinds and counts) and the outside domains.
        await this.Svc.LogPatientWarningAsync("arun@anjal.co.in", ThreeOutside, sentAnyway: true);
        await this.Svc.LogPatientWarningAsync("arun@anjal.co.in", OneOutside, sentAnyway: false);
        await this.Svc.LogPatientWarningAsync("arun@anjal.co.in", NoAddress, sentAnyway: true);
        IReadOnlyList<AuditEvent> log = await this.store.ListAuditAsync(50);
        AuditEvent sent = Assert.Single(log, e => e.Action == "webmail.patient-warning.sent");
        Assert.Equal("gmail.test, city-lab.test", sent.Subject);
        Assert.Equal("text: phone number ×2, record number, a file", sent.Detail);
        Assert.DoesNotContain("Ramesh", sent.Detail, StringComparison.Ordinal);
        AuditEvent shown = Assert.Single(log, e => e.Action == "webmail.patient-warning.shown");
        Assert.Equal("gmail.test", shown.Subject);
    }

    [Fact]
    public async Task EmptyDrafts_GoAfterThirtyDays_NeverUnderAHold_AndAWrittenDraftStays()
    {
        Assert.True(await this.Svc.IsEmptyComposeAsync(this.arun.Id, new ComposeRequest()));
        Assert.True(await this.Svc.IsEmptyComposeAsync(this.arun.Id, new ComposeRequest { BodyHtml = "<p> </p><p><br></p>" }));
        Assert.False(await this.Svc.IsEmptyComposeAsync(this.arun.Id, new ComposeRequest { Subject = "Rota" }));
        Assert.False(await this.Svc.IsEmptyComposeAsync(this.arun.Id, new ComposeRequest { Body = "See you at 10." }));

        DateTimeOffset old = DateTimeOffset.UtcNow.AddDays(-31);
        MessageRow oldEmpty = await this.DraftAsync(this.arun, old, empty: true);
        MessageRow newEmpty = await this.DraftAsync(this.arun, DateTimeOffset.UtcNow.AddDays(-2), empty: true);
        MessageRow oldWritten = await this.DraftAsync(this.arun, old, empty: false);
        MessageRow held = await this.DraftAsync(this.ravi, old, empty: true);
        Assert.Null(await this.Svc.SetHoldAsync(this.tenant, this.ravi.Id, true, "arun@anjal.co.in", "Case 14"));

        Assert.Equal(1, await this.Svc.RemoveEmptyDraftsAsync());
        var left = this.store.MailboxMessages.Select(m => m.Id).ToHashSet();
        Assert.DoesNotContain(oldEmpty.Id, left);
        Assert.Contains(newEmpty.Id, left);
        Assert.Contains(oldWritten.Id, left);
        Assert.Contains(held.Id, left);
        Assert.Contains(await this.store.ListAuditAsync(50), e => e.Action == "drafts.empty.removed" && e.Detail.StartsWith("1 ", StringComparison.Ordinal));
        Assert.Equal(0, await this.Svc.RemoveEmptyDraftsAsync());
    }

    [Fact]
    public async Task TheConsole_HasFourCharts_AndRecordsTheScreenReaderCheck()
    {
        string? before = Environment.GetEnvironmentVariable("ANJAL_OPERATORS");
        try
        {
            Environment.SetEnvironmentVariable("ANJAL_OPERATORS", "arun@anjal.co.in");
            HttpClient op = this.Client();
            await SignInAsync(op, "arun");
            // rc.27 (item 72.7): four small charts, each on its own scale, each with its last value.
            string overview = await op.GetStringAsync("ops");
            Assert.Equal(4, Regex.Count(overview, "<svg class=\"aline\""));
            Assert.Equal(4, Regex.Count(overview, "<span class=\"aend\">"));

            // rc.27 (item 79, D-312): the monthly check is recorded like the restore drill.
            string health = await op.GetStringAsync("ops/health");
            Assert.Contains("Screen-reader check", health, StringComparison.Ordinal);
            Assert.Contains("None recorded yet.", health, StringComparison.Ordinal);
            HttpResponseMessage bad = await Post(op, "ops/reader-check", Token(health), ("date", "28-10-2026"), ("reader", "NVDA"), ("browser", "Chrome"), ("result", "passed"));
            Assert.Equal("/ops/health?saved=reader-date", bad.Headers.Location!.ToString());
            DateTime day = DateTime.UtcNow.Date;
            HttpResponseMessage res = await Post(op, "ops/reader-check", Token(health), ("date", day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)), ("reader", "NVDA"), ("browser", "Chrome"), ("result", "failed"), ("findings", "The desk's tabs read as a list"));
            Assert.Equal("/ops/health?saved=reader", res.Headers.Location!.ToString());
            ReaderCheck check = Assert.Single(await this.Svc.ReaderChecksAsync());
            Assert.False(check.Passed);
            Assert.Equal("NVDA", check.Reader);
            Assert.Equal("The desk's tabs read as a list", check.Findings);
            Assert.Equal(day.AddMonths(1), MailboxService.NextReaderCheckDue(new[] { check }));
            Assert.Equal(10, ReaderCheck.Steps.Count);
            Assert.Contains(await this.store.ListAuditAsync(50), e => e.Action.StartsWith("ops.reader-check", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANJAL_OPERATORS", before);
        }
    }

    [Fact]
    public async Task TheMondaySummary_GivesSpamPerOrganisation()
    {
        string today = DateTimeOffset.UtcNow.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        string old = DateTimeOffset.UtcNow.AddDays(-20).ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var record = new Dictionary<string, Dictionary<string, long>>
        {
            [today] = new() { ["spam-refused|anjal.co.in"] = 3, ["spam-refused|elsewhere.test"] = 9 },
            [old] = new() { ["spam-refused|anjal.co.in"] = 50 },
        };
        await this.store.SetServiceRecordAsync("refusals-by-domain", JsonSerializer.Serialize(record));
        IReadOnlyList<OrgSummary> orgs = await this.Svc.ListOrganisationsAsync();
        string text = await this.Svc.OrgSpamWeekTextAsync(orgs, DateTimeOffset.UtcNow.AddDays(-7));
        Assert.Contains("By organisation", text, StringComparison.Ordinal);
        Assert.Contains("Imagiqa: refused 3, put in Junk 0, \"Not junk\" 0", text, StringComparison.Ordinal);
        await this.store.SetServiceRecordAsync("refusals-by-domain", "{}");
        Assert.Equal(string.Empty, await this.Svc.OrgSpamWeekTextAsync(orgs, DateTimeOffset.UtcNow.AddDays(-7)));
    }

    [Fact]
    public async Task ShortcutLetters_ComeFromTheOneMap()
    {
        HttpClient c = this.Client();
        await SignInAsync(c, "arun");
        string inbox = await c.GetStringAsync("folder/INBOX");
        Match m = Regex.Match(inbox, "data-kbmap=\"([^\"]+)\"");
        Assert.True(m.Success);
        using JsonDocument map = JsonDocument.Parse(WebUtility.HtmlDecode(m.Groups[1].Value));
        Assert.Equal("r", map.RootElement.GetProperty("reply").GetString());
        Assert.Equal("a", map.RootElement.GetProperty("replyall").GetString());
        Assert.Equal("f", map.RootElement.GetProperty("forward").GetString());
        Assert.Equal("e", map.RootElement.GetProperty("archive").GetString());
        Assert.Equal("#", map.RootElement.GetProperty("delete").GetString());
        Assert.Equal("/", map.RootElement.GetProperty("search").GetString());
        // rc.27 (item 79): the skip links of a mail page.
        Assert.Contains("Skip to the mail list", inbox, StringComparison.Ordinal);
    }

    [Fact]
    public void ReducedMotion_StopsEveryAnimation_PseudoElementsToo()
    {
        string css = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anjal.Webmail", "wwwroot", "app.css"));
        Assert.Contains("@media (prefers-reduced-motion: reduce) { *, *::before, *::after { transition: none !important; animation: none !important; } }", css, StringComparison.Ordinal);
        // rc.27 (item 79): forced colours (Windows High Contrast) have their own rules.
        Assert.Contains("@media (forced-colors: active)", css, StringComparison.Ordinal);
    }

    private async Task<MessageRow> DraftAsync(MailboxRow who, DateTimeOffset at, bool empty)
    {
        await this.Svc.ListFoldersAsync(who.Id);
        FolderRow drafts = (await this.Svc.GetFolderAsync(who.Id, "Drafts"))!;
        MessageRow row = new()
        {
            MailboxId = who.Id,
            FolderId = drafts.Id,
            Subject = empty ? string.Empty : "Rota",
            MaildirFile = "draft-" + Guid.NewGuid().ToString("N"),
            DraftEmpty = empty,
        };
        Func<DateTimeOffset> clock = this.store.MessageClock;
        this.store.MessageClock = () => at;
        try
        {
            return await this.store.SaveMessageAsync(row);
        }
        finally
        {
            this.store.MessageClock = clock;
        }
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

    private static async Task SignInAsync(HttpClient c, string user)
    {
        string token = Token(await c.GetStringAsync("sign-in"));
        HttpResponseMessage res = await Post(c, "auth/login", token, ("address", user), ("password", "correct horse battery"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
    }

    private Task<MailboxRow> Person(string local, string name) =>
        this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = this.tenant.Id,
            LocalPart = local,
            Domain = "anjal.co.in",
            DisplayName = name,
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        });

    private HttpClient Client()
    {
        var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(this.baseAddress + "/") };
        this.clients.Add(c);
        return c;
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
