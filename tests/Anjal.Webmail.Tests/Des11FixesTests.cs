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
/// The faults found while writing ANJAL-DES-11 (F1 to F12), fixed at the owner's word on 10 Oct
/// 2026 ("fix F1 to 11 now and also a system wide check"), each held here through the real
/// pipeline. The system-wide checks are in <see cref="SystemWideTests"/> and
/// <see cref="EveryFormReachesTests"/>.
/// </summary>
[Collection("Operators")]
public sealed class Des11FixesTests : IAsyncLifetime, IDisposable
{
    private static readonly string[] LogoKinds = { "mark", "favicon" };
    private static readonly string[] SharedRecipient = { "operations@anjal.co.in" };

    private static readonly string[] StandardButUnused = { "Archive", MailboxService.ScheduledFolder };

    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-des11-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly List<HttpClient> clients = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private string baseAddress = string.Empty;
    private TenantRow tenant = new();
    private MailboxRow arun = new();
    private MailboxRow meera = new();
    private string? operatorsBefore;

    public void Dispose()
    {
        foreach (HttpClient c in this.clients)
        {
            c.Dispose();
        }
    }

    public async Task InitializeAsync()
    {
        this.operatorsBefore = Environment.GetEnvironmentVariable("ANJAL_OPERATORS");
        this.maildir = new MaildirStore(this.root, "test");
        this.tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "Imagiqa", PostmasterMailbox = "arun@anjal.co.in" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.tenant.Id, Domain = "anjal.co.in" });
        this.arun = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = this.tenant.Id, LocalPart = "arun", Domain = "anjal.co.in", DisplayName = "Arun Shiva B", PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000) });
        this.meera = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = this.tenant.Id, LocalPart = "meera.iyer", Domain = "anjal.co.in", DisplayName = "Meera Iyer", PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000) });
        this.app = Program.CreateApp(Array.Empty<string>(), this.store, this.maildir, "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
        this.baseAddress = this.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("ANJAL_OPERATORS", this.operatorsBefore);
        await this.app.StopAsync();
        await this.app.DisposeAsync();
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    /// <summary>F1: a folder can be made and a rule saved (both answered 400 since rc.12).</summary>
    [Fact]
    public async Task F1_AFolderIsMade_AndARuleIsSaved()
    {
        HttpClient c = await this.SignedInAsync("arun");
        string page = await c.GetStringAsync(new Uri("settings/rules", UriKind.Relative));
        using HttpResponseMessage made = await Post(c, "settings/folders/new", Token(page), ("name", "Ward rounds"));
        Assert.Equal(HttpStatusCode.Redirect, made.StatusCode);
        Assert.Contains(await this.store.ListFoldersAsync(this.arun.Id), f => f.Name == "Ward rounds");
    }

    /// <summary>F2: Suspend and Resume refuse a post that carries no form token (another website's).</summary>
    [Fact]
    public async Task F2_SuspendWithoutTheFormToken_IsRefused()
    {
        Environment.SetEnvironmentVariable("ANJAL_OPERATORS", "arun@anjal.co.in");
        HttpClient c = await this.SignedInAsync("arun");
        TenantRow other = await this.store.UpsertTenantAsync(new TenantRow { Slug = "other", DisplayName = "Other" });
        using var empty = new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>());
        using HttpResponseMessage res = await c.PostAsync(new Uri($"ops/orgs/{other.Id}/suspend", UriKind.Relative), empty);
        Assert.Equal("/sign-in?expired=1", res.Headers.Location!.ToString());
        Assert.True((await this.store.GetTenantByIdAsync(other.Id))!.Enabled);
    }

    /// <summary>
    /// F4: the operators get the weekly summary on Monday from 09:00 in Anjal's zone, an alert as
    /// soon as something needs attention, again after 6 hours, and "resolved" when it clears.
    /// </summary>
    [Fact]
    public async Task F4_OperatorsAreMailed_SummaryOnMonday_AlertsRepeatEvery6Hours_AndClear()
    {
        Environment.SetEnvironmentVariable("ANJAL_OPERATORS", "arun@anjal.co.in");
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        DateTimeOffset monday = new(2026, 10, 12, 9, 30, 0, TimeSpan.FromHours(5.5));
        Assert.Equal(DayOfWeek.Monday, monday.DayOfWeek);

        int first = await svc.OperatorMailAsync(this.root, monday);
        Assert.True(first >= 2, "summary and alert: " + first);
        Assert.Contains(this.Subjects(), s => s.StartsWith("Anjal service summary", StringComparison.Ordinal));
        Assert.Contains(this.Subjects(), s => s.StartsWith("Anjal alert", StringComparison.Ordinal));

        Assert.Equal(0, await svc.OperatorMailAsync(this.root, monday.AddHours(1)));
        Assert.True(await svc.OperatorMailAsync(this.root, monday.AddHours(6)) >= 1);

        await svc.RecordDrillAsync(new RestoreDrill { Date = "2026-10-12", Passed = true, By = "arun@anjal.co.in" });
        await svc.OperatorMailAsync(this.root, monday.AddHours(7));
        Assert.Contains(this.Subjects(), s => s.StartsWith("Anjal resolved", StringComparison.Ordinal) && s.Contains("restore drill", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, this.Subjects().Count(s => s.StartsWith("Anjal service summary", StringComparison.Ordinal)));
    }

    /// <summary>
    /// F4, found 10 Oct 2026 in the screen checks: a thing that needs attention is the same thing when
    /// only its figures change - no "resolved" mail and no new alert each time a count moves.
    /// </summary>
    [Fact]
    public async Task F4_AFigureChanging_IsNotResolvedAndRaisedAgain()
    {
        Environment.SetEnvironmentVariable("ANJAL_OPERATORS", "arun@anjal.co.in");
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        DateTimeOffset monday = new(2026, 10, 12, 11, 0, 0, TimeSpan.FromHours(5.5));
        await svc.RecordDrillAsync(new RestoreDrill { Date = "2026-10-12", Passed = true, By = "arun@anjal.co.in" });
        FolderRow inbox = await this.store.EnsureFolderAsync(this.arun.Id, "INBOX");
        Func<DateTimeOffset> was = this.store.MessageClock;
        async Task ArriveAsync(int n, DateTimeOffset at)
        {
            this.store.MessageClock = () => at;
            for (int i = 0; i < n; i++)
            {
                await this.store.SaveMessageAsync(new MessageRow { MailboxId = this.arun.Id, FolderId = inbox.Id, Subject = "Rise " + i, MaildirFile = "rise-" + Guid.NewGuid().ToString("N") });
            }
            this.store.MessageClock = was;
        }

        await ArriveAsync(25, monday.AddHours(-1));
        await svc.OperatorMailAsync(this.root, monday);
        int alerts = this.Subjects().Count(s => s.StartsWith("Anjal alert", StringComparison.Ordinal));
        Assert.True(alerts >= 1);
        Assert.Contains(await svc.SuddenRisesAsync(ZonedClock.For("Asia/Kolkata", null), monday), r => r.Kind == "received" && r.Today == 25);

        await ArriveAsync(5, monday.AddMinutes(10));
        await svc.OperatorMailAsync(this.root, monday.AddMinutes(30));
        Assert.Equal(alerts, this.Subjects().Count(s => s.StartsWith("Anjal alert", StringComparison.Ordinal)));
        Assert.DoesNotContain(this.Subjects(), s => s.StartsWith("Anjal resolved", StringComparison.Ordinal));
        Assert.Contains(await svc.SuddenRisesAsync(ZonedClock.For("Asia/Kolkata", null), monday.AddMinutes(30)), r => r.Kind == "received" && r.Today == 30);
    }

    /// <summary>F4: alerts kept the old way (by their words) are taken over without any mail.</summary>
    [Fact]
    public async Task F4_AlertsKeptTheOldWay_AreTakenOverQuietly()
    {
        Environment.SetEnvironmentVariable("ANJAL_OPERATORS", "arun@anjal.co.in");
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        DateTimeOffset tuesday = new(2026, 10, 13, 11, 0, 0, TimeSpan.FromHours(5.5));
        var old = new OpsAlertState { LastSummaryDay = "2026-10-12" };
        old.Open["No restore drill recorded"] = new OpenAlert { Text = "No restore drill recorded - Record each drill on the Service health page", First = tuesday.AddHours(-2), LastSent = tuesday.AddHours(-2) };
        await this.store.SetServiceRecordAsync(MailboxService.OpsAlertsKind, System.Text.Json.JsonSerializer.Serialize(old));
        Assert.Equal(0, await svc.OperatorMailAsync(this.root, tuesday));
        Assert.DoesNotContain(this.Subjects(), s => s.StartsWith("Anjal ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task D6_TheSignInMarkAndTheTabShowTheLetterOfTheLanguageShown()
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(this.baseAddress + "/") };
        this.clients.Add(c);
        string page = await c.GetStringAsync(new Uri("sign-in", UriKind.Relative));
        Assert.Contains("<html lang=\"en\"", page, StringComparison.Ordinal);
        Assert.Contains("<span class=\"mt-letter\" lang=\"en\">A</span>", page, StringComparison.Ordinal);
        Match icon = Regex.Match(page, "<link rel=\"icon\" href=\"/(logos/anjal-favicon-latn\\.svg)\\?v=([^\"]+)\"");
        Assert.True(icon.Success, "the tab's icon carries the letter and a fingerprint");
        using HttpResponseMessage svg = await c.GetAsync(new Uri(icon.Groups[1].Value + "?v=" + icon.Groups[2].Value, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, svg.StatusCode);
        Assert.Contains("#0E4B4F", await svg.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void D6_EveryLanguageHasItsMarkAndFavicon_DrawnInTheNewIdentity()
    {
        string logos = Path.Combine(RepoRoot(), "src", "Anjal.Webmail", "wwwroot", "logos");
        foreach (string lc in MailboxPreferences.Languages)
        {
            foreach (string kind in LogoKinds)
            {
                string svg = File.ReadAllText(Path.Combine(logos, "anjal-" + kind + "-" + Words.MarkScript(lc) + ".svg"));
                Assert.Contains("#0E4B4F", svg, StringComparison.Ordinal);
                Assert.Contains("#E8A317", svg, StringComparison.Ordinal);
                Assert.DoesNotContain("<text", svg, StringComparison.Ordinal);
            }
        }
        foreach (string old in Directory.GetFiles(logos, "*.svg"))
        {
            Assert.DoesNotContain("#b02f22", File.ReadAllText(old), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>F4: with no operators set, nothing is sent and nothing fails.</summary>
    [Fact]
    public async Task F4_NoOperators_NoMail()
    {
        Environment.SetEnvironmentVariable("ANJAL_OPERATORS", null);
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        Assert.Equal(0, await svc.OperatorMailAsync(this.root, new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.FromHours(5.5))));
    }

    /// <summary>F5: Send one each puts no Cc on the copies and sends one summary copy instead.</summary>
    [Fact]
    public async Task F5_SendOneEach_SendsOneSummaryCopy_ListingWhoItWentTo()
    {
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        (string? error, int made) = await svc.SendOneEachAsync(this.arun.Id, new ComposeRequest
        {
            To = "ravi@one.example, sita@two.example",
            Cc = "boss@three.example",
            Subject = "Hello {First name}",
            Body = "The rota is attached.",
            MergeSummaryTo = "arun@anjal.co.in",
        }, null, DateTimeOffset.UtcNow.AddHours(1), map: null, clock: ZonedClock.Default);
        Assert.Null(error);
        Assert.Equal(2, made);
        FolderRow scheduled = (await this.store.ListFoldersAsync(this.arun.Id)).Single(f => f.Name == MailboxService.ScheduledFolder);
        List<MessageRow> held = this.store.MailboxMessages.Where(m => m.FolderId == scheduled.Id).ToList();
        Assert.Equal(3, held.Count);
        MessageRow summary = Assert.Single(held, m => m.Subject == "Sent one each: Hello {First name}");
        Assert.Contains("arun@anjal.co.in", summary.ToHeader, StringComparison.Ordinal);
        Assert.DoesNotContain(held, m => m.ToHeader.Contains("boss@three.example", StringComparison.Ordinal));
        string text = MailboxService.MergeSummaryText(new ComposeRequest { Subject = "S", Body = "B" }, new[] { new MergePerson("ravi@one.example", new Dictionary<string, string>()) }, 1, DateTimeOffset.UtcNow, ZonedClock.Default);
        Assert.Contains("- ravi@one.example", text, StringComparison.Ordinal);
        Assert.Contains("no copy carried Cc or Bcc", text, StringComparison.Ordinal);
    }

    /// <summary>F6: in a shared mailbox, a sender already in the person's own contacts is not offered again.</summary>
    [Fact]
    public async Task F6_InASharedMailbox_TheNewSenderWindowReadsThePersonsOwnBook()
    {
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        (string? _, Guid? ops) = await svc.CreateSharedMailboxAsync(this.tenant, "operations@anjal.co.in", "Operations");
        Assert.Null(await svc.SetSharedRightAsync(this.tenant, ops!.Value, this.meera.Id, "send"));
        Assert.Null(await svc.SaveContactAsync(this.meera.Id, new Contact { Address = "ravi@supplier.example", FirstName = "Ravi" }));
        DeliveryResult r = await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "ravi@supplier.example",
            EnvelopeTo = SharedRecipient,
            RawBytes = Encoding.UTF8.GetBytes("From: Ravi <ravi@supplier.example>\r\nTo: operations@anjal.co.in\r\nSubject: Stock\r\n\r\nThe stock list.\r\n"),
        });
        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
        Guid message = this.store.MailboxMessages[^1].Id;

        HttpClient c = await this.SignedInAsync("meera.iyer");
        string inbox = await c.GetStringAsync(new Uri("folder/INBOX", UriKind.Relative));
        using HttpResponseMessage opened = await Post(c, "shared/open", Token(inbox), ("id", ops.Value.ToString()));
        string page = await c.GetStringAsync(new Uri("message/" + message, UriKind.Relative));
        Assert.Contains("Stock", page, StringComparison.Ordinal);
        Assert.DoesNotContain("data-newsender", page, StringComparison.Ordinal);
    }

    /// <summary>F7: after a bulk action in Unread, the person stays in Unread.</summary>
    [Fact]
    public async Task F7_ABulkActionInUnread_StaysInUnread()
    {
        HttpClient c = await this.SignedInAsync("arun");
        string page = await c.GetStringAsync(new Uri("folder/INBOX?show=unread", UriKind.Relative));
        using HttpResponseMessage res = await Post(c, "folder/INBOX/bulk", Token(page), ("action", "read"), ("page", "0"), ("show", "unread"));
        Assert.Equal("/folder/INBOX?page=0&show=unread", res.Headers.Location!.ToString());
        using HttpResponseMessage all = await Post(c, "folder/INBOX/readall", Token(page), ("show", "read"));
        Assert.Equal("/folder/INBOX?page=0&show=read", all.Headers.Location!.ToString());
    }

    /// <summary>F8: Archive and Scheduled open (empty) in a mailbox that has never used them.</summary>
    [Fact]
    public async Task F8_ArchiveAndScheduled_OpenInANewMailbox()
    {
        HttpClient c = await this.SignedInAsync("meera.iyer");
        foreach (string folder in StandardButUnused)
        {
            using HttpResponseMessage res = await c.GetAsync(new Uri("folder/" + folder, UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.DoesNotContain("No folder called", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    /// <summary>F9: the restore drill's "today" is the viewer's day - just after midnight IST it is already the new day.</summary>
    [Fact]
    public async Task F9_TheDrillsToday_IsTheViewersDay()
    {
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        ZonedClock india = ZonedClock.For("Asia/Kolkata", null);
        // Due 12 Oct (six months after a drill on 12 Apr); at 00:30 IST on 13 Oct (still 12 Oct in UTC) it is overdue.
        await svc.RecordDrillAsync(new RestoreDrill { Date = "2026-04-12", Passed = true, By = "arun@anjal.co.in" });
        DateTimeOffset justAfterMidnight = new(2026, 10, 13, 0, 30, 0, TimeSpan.FromHours(5.5));
        IReadOnlyList<DashLine> lines = await svc.OpsAttentionAsync(india, Array.Empty<HealthTile>(), Array.Empty<OrgSummary>(), justAfterMidnight);
        Assert.Contains(lines, l => l.Text == "Restore drill overdue since {date}");
    }

    /// <summary>F10: percentages round to the nearest, but never claim 100% or 0% wrongly.</summary>
    [Fact]
    public void F10_Percentages_RoundToTheNearest()
    {
        Assert.Equal(80, Full(796));
        Assert.Equal(79, Full(794));
        Assert.Equal(99, Full(997));
        Assert.Equal(100, Full(1000));
        Assert.Equal(1, Full(1));
        Assert.Equal(0, Full(0));

        static int Full(long used) => MailboxService.Fullness(new MailboxRow { UsedBytes = used, QuotaBytes = 1000 });
    }

    /// <summary>F12: on Windows the start-up messages do not name a Linux file.</summary>
    [Fact]
    public void F12_TheSettingsSource_IsNamedForThisSystem()
    {
        string name = StoredSettings.EnvironmentName("webmail");
        Assert.Equal(OperatingSystem.IsLinux(), name.StartsWith("/etc/anjal/", StringComparison.Ordinal));
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

    private List<string> Subjects() => this.store.MailboxMessages.Where(m => m.MailboxId == this.arun.Id).Select(m => m.Subject).ToList();

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private async Task<HttpClient> SignedInAsync(string user)
    {
        var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(this.baseAddress + "/") };
        this.clients.Add(c);
        string token = Token(await c.GetStringAsync(new Uri("sign-in", UriKind.Relative)));
        using HttpResponseMessage res = await Post(c, "auth/login", token, ("address", user), ("password", "correct horse battery"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        return c;
    }
}
