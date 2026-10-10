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
/// rc.15 Batch C (items 56 to 61; owner, 7 and 8 Oct 2026): every dashboard number opens what it
/// counts; the person's Junk rescued, space by folder and "Your habits"; the organisation's daily
/// DNS check, TLS reports, sign-ins from new networks (with town and country from the DB-IP list)
/// and what was refused for its applications; figures per person only with a purpose and with
/// everyone told; and the operator's disk forecast, refusals and restore drills.
/// </summary>
public sealed partial class DashboardsBatchCTests : IAsyncLifetime, IDisposable
{
    private const string Slug = "dashc";
    private static readonly string[] ArunRecipient = { "arun@dashc.example" };
    private static readonly string[] OurMx = { "anjal.localhost." };
    private static readonly string[] AppKeys = { "APP-1@dashc.example" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-dashc-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly List<HttpClient> clients = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private string baseAddress = string.Empty;
    private TenantRow tenant = null!;
    private MailboxRow arun = null!;
    private MailboxRow meera = null!;

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
        this.tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = Slug, DisplayName = "Dash Clinic", PostmasterMailbox = "arun@dashc.example" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.tenant.Id, Domain = "dashc.example" });
        this.arun = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = this.tenant.Id, LocalPart = "arun", Domain = "dashc.example", DisplayName = "Arun", PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000) });
        this.meera = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = this.tenant.Id, LocalPart = "meera", Domain = "dashc.example", DisplayName = "Meera", PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000) });
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
    public void ATlsReport_IsRead_AndAnythingElseIsNot()
    {
        const string json = "{\"organization-name\":\"Google Inc.\",\"date-range\":{\"start-datetime\":\"2026-10-07T00:00:00Z\",\"end-datetime\":\"2026-10-07T23:59:59Z\"},"
            + "\"report-id\":\"x\",\"policies\":[{\"policy\":{\"policy-type\":\"sts\",\"policy-domain\":\"DashC.example\"},"
            + "\"summary\":{\"total-successful-session-count\":41,\"total-failure-session-count\":2},"
            + "\"failure-details\":[{\"result-type\":\"certificate-expired\",\"failed-session-count\":2}]}]}";
        using var packed = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(packed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            gz.Write(Encoding.UTF8.GetBytes(json));
        }
        IReadOnlyList<TlsReportRow>? rows = TlsReports.Read(packed.ToArray());
        Assert.NotNull(rows);
        TlsReportRow row = Assert.Single(rows);
        Assert.Equal("Google Inc.", row.Reporter);
        Assert.Equal("dashc.example", row.Domain);
        Assert.Equal(41, row.Successful);
        Assert.Equal(2, row.Failed);
        Assert.Equal("certificate-expired", row.Failures);
        Assert.Null(TlsReports.Read(Encoding.UTF8.GetBytes("<feedback/>")));
        Assert.Null(TlsReports.Read(Encoding.UTF8.GetBytes("{\"no\":\"policies\"}")));
        Assert.True(TlsReports.MayBeReport("google.com!dashc.example!1!2!x.json.gz", "application/tlsrpt+gzip"));
    }

    [Fact]
    public async Task TheLocationList_IsPacked_AndFindsTownAndCountry()
    {
        string csv = "1.0.0.0,1.0.0.255,OC,AU,Queensland,\"South Brisbane, Qld\",-27.4,153.0\n"
            + "1.0.1.0,1.0.3.255,AS,CN,Fujian,Fuzhou,26.0,119.3\n"
            + "49.36.0.0,49.36.255.255,AS,IN,Maharashtra,Pune,18.5,73.8\n"
            + "2001:db8::,2001:db8:ffff:ffff:ffff:ffff:ffff:ffff,AS,IN,Kerala,Thiruvananthapuram,8.5,76.9\n";
        string file = Path.Combine(this.root, "ip-locations.bin");
        Directory.CreateDirectory(this.root);
        using (var reader = new StringReader(csv))
        {
            Assert.Equal(4, await IpLocations.BuildAsync(reader, file, DateTimeOffset.UtcNow));
        }
        Assert.Equal("Pune, India", IpLocations.Find(IPAddress.Parse("49.36.10.20"), file)?.Text);
        Assert.Equal("South Brisbane, Qld", IpLocations.Find(IPAddress.Parse("1.0.0.7"), file)?.City);
        Assert.Equal("Thiruvananthapuram", IpLocations.Find(IPAddress.Parse("2001:db8::5"), file)?.City);
        Assert.Null(IpLocations.Find(IPAddress.Parse("0.255.0.1"), file));
        Assert.NotNull(IpLocations.BuiltAt(file));
        Assert.True(IpLocations.IsPrivate(IPAddress.Parse("192.168.1.4")));
        Assert.True(IpLocations.IsPrivate(IPAddress.Loopback));
        Assert.False(IpLocations.IsPrivate(IPAddress.Parse("49.36.10.20")));
        Assert.Equal("49.36.10.0/24", IpLocations.NetworkOf(IPAddress.Parse("49.36.10.20")));
    }

    [Fact]
    public async Task ANewNetwork_IsNotedOnlyAfterTheFirstSignIn_AndNeverForAPrivateOne()
    {
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.False((await svc.NoteSignInNetworkAsync(this.meera.Id, "203.0.113.5", now)).IsNew);
        Assert.False((await svc.NoteSignInNetworkAsync(this.meera.Id, "203.0.113.77", now)).IsNew);
        Assert.True((await svc.NoteSignInNetworkAsync(this.meera.Id, "198.51.100.7", now)).IsNew);
        Assert.False((await svc.NoteSignInNetworkAsync(this.meera.Id, "10.1.2.3", now)).IsNew);

        await this.store.AppendAuditAsync(new AuditEvent { Actor = this.meera.Address, Action = "webmail.signin.new-network", Subject = this.meera.Address, Detail = "Pune, India" });
        HttpClient a = await this.SignedInAsync("arun");
        string dash = await a.GetStringAsync(new Uri("dashboard/org", UriKind.Relative));
        Assert.Contains("1 sign-in from a new network today", dash, StringComparison.Ordinal);
        Assert.Contains("Meera, Pune, India", dash, StringComparison.Ordinal);
        Assert.Contains(IpLocations.Credit, dash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePersonsDashboard_ShowsJunkRescued_SpaceByFolder_AndHabits_AndEveryNumberOpens()
    {
        await this.DeliverAsync("From: Lab <lab@x.example>\r\nTo: arun@dashc.example\r\nSubject: Report ready\r\nDate: " + DateTimeOffset.UtcNow.ToString("r") + "\r\n\r\nYour report is ready.\r\n");
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        MessageRow got = (await this.store.ListMessagesAsync(this.arun.Id, (await svc.GetFolderAsync(this.arun.Id, FolderRow.Inbox))!.Id, 10, 0)).Single();
        await svc.MoveAsync(this.arun.Id, got.Id, MailboxSink.JunkFolder);
        await svc.MarkNotSpamAsync(this.arun.Id, got.Id);

        HttpClient c = await this.SignedInAsync("arun");
        string dash = await c.GetStringAsync(new Uri("dashboard", UriKind.Relative));
        Assert.Contains("1 message rescued from Junk in the last 30 days", dash, StringComparison.Ordinal);
        Assert.Contains("href=\"/search?scope=all&amp;rescued=30\"", dash, StringComparison.Ordinal);
        Assert.Contains("Space by folder", dash, StringComparison.Ordinal);
        Assert.Matches(MbRegex(), dash);
        Assert.Contains("/search?scope=all&amp;sort=size", dash, StringComparison.Ordinal);
        Assert.Contains("class=\"hourbars\"", dash, StringComparison.Ordinal);

        string rescued = await c.GetStringAsync(new Uri("search?scope=all&rescued=30", UriKind.Relative));
        Assert.Contains("Report ready", rescued, StringComparison.Ordinal);
        Assert.Contains("rescued from Junk in the last 30 days", rescued, StringComparison.Ordinal);
        Assert.Contains("biggest first", await c.GetStringAsync(new Uri("search?scope=all&sort=size", UriKind.Relative)), StringComparison.Ordinal);

        // Habits are on unless the person turns them off; off, nothing is worked out.
        using (HttpResponseMessage r = await Post(c, "settings/habits", Token(dash), ("on", "0"), ("back", "/dashboard")))
        {
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        }
        string off = await c.GetStringAsync(new Uri("dashboard", UriKind.Relative));
        Assert.Contains("Off. Nothing is worked out about how you use your mail.", off, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"hourbars\"", off, StringComparison.Ordinal);
        Assert.False((await svc.GetMailSettingsAsync(this.arun.Id)).Habits);
    }

    [Fact]
    public async Task FiguresPerPerson_NeedAPurpose_TellEveryone_AndAreLogged()
    {
        HttpClient a = await this.SignedInAsync("arun");
        string page = await a.GetStringAsync(new Uri("org/figures", UriKind.Relative));
        Assert.Contains("Turn on and tell everyone", page, StringComparison.Ordinal);
        using (HttpResponseMessage r = await Post(a, "org/figures", Token(page), ("action", "on"), ("purpose", "short"), ("from", "30d")))
        {
            Assert.Contains("e=", r.Headers.Location!.ToString(), StringComparison.Ordinal);
        }
        Assert.False((await this.app.Services.GetRequiredService<MailboxService>().PersonFiguresOfAsync(this.tenant.Id)).On);

        using (HttpResponseMessage r = await Post(a, "org/figures", Token(page), ("action", "on"), ("purpose", "To plan the front desk's workload"), ("from", "90d")))
        {
            Assert.Equal("/org/figures?saved=1", r.Headers.Location!.ToString());
        }
        Assert.Contains(await this.store.ListAuditAsync(50), e => e.Action == "org.figures.on" && e.Detail.Contains("front desk", StringComparison.Ordinal));
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        IReadOnlyList<MessageRow> meeraInbox = await this.store.ListMessagesAsync(this.meera.Id, (await svc.GetFolderAsync(this.meera.Id, FolderRow.Inbox))!.Id, 10, 0);
        Assert.Contains(meeraInbox, m => m.Subject.Contains("now counts mail figures for each person", StringComparison.Ordinal));

        string dash = await a.GetStringAsync(new Uri("dashboard/org", UriKind.Relative));
        Assert.Contains("id=\"o-people\"", dash, StringComparison.Ordinal);
        Assert.Contains("<b>Meera</b>", dash, StringComparison.Ordinal);
        HttpClient m = await this.SignedInAsync("meera");
        Assert.Contains("To plan the front desk&#x27;s workload", await m.GetStringAsync(new Uri("settings/security", UriKind.Relative)), StringComparison.Ordinal);

        using (HttpResponseMessage r = await Post(a, "org/figures", Token(page), ("action", "off")))
        {
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        }
        Assert.Contains(await this.store.ListAuditAsync(50), e => e.Action == "org.figures.off");
        Assert.DoesNotContain("<b>Meera</b>", await a.GetStringAsync(new Uri("dashboard/org", UriKind.Relative)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDailyDnsCheck_KeepsWhatItFound_AndAWrongRecordNeedsAttention()
    {
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.True(await svc.CheckDnsDailyAsync(now, name => Task.FromResult<IReadOnlyList<string>?>(name.StartsWith("MX:", StringComparison.Ordinal) ? OurMx : Array.Empty<string>())) >= 1);
        DnsCheckRecord? check = await svc.DnsCheckOfAsync(this.tenant.Id);
        Assert.NotNull(check);
        Assert.Equal("pass", check.Domains["dashc.example"]["MX"]);
        Assert.Equal("missing", check.Domains["dashc.example"]["SPF"]);

        // Within a day it is not asked again.
        Assert.Equal(0, await svc.CheckDnsDailyAsync(now.AddHours(1), _ => throw new InvalidOperationException("asked again")));

        HttpClient a = await this.SignedInAsync("arun");
        string dash = await a.GetStringAsync(new Uri("dashboard/org", UriKind.Relative));
        Assert.Contains("dashc.example: SPF, DKIM, DMARC, MTA-STS not right", dash, StringComparison.Ordinal);
        Assert.Contains("Your domains&#x27; records", dash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnApplicationsRefusals_AreCounted_AndTheOperatorsRecordsAddUp()
    {
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        await this.store.AppendAuditAsync(new AuditEvent { Actor = "app-1@dashc.example", Action = "smtp.submission.refused", Subject = "app-1@dashc.example", Detail = "wrong password" });
        await this.store.AppendAuditAsync(new AuditEvent { Actor = "app-1@dashc.example", Action = "smtp.submission.refused", Subject = "app-1@dashc.example", Detail = "not allowed to send as x@y" });
        IReadOnlyDictionary<string, int> refused = await svc.RefusedSubmissionsAsync(AppKeys, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(2, refused["app-1@dashc.example"]);

        await this.store.SetServiceRecordAsync(MailboxService.RefusalsKind, "{\"2000-01-01\":{\"anjal_smtp_auth_failures_total\":99},\"" + DateTimeOffset.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + "\":{\"anjal_smtp_auth_failures_total\":7,\"anjal_spam_rejected_total\":3}}");
        IReadOnlyDictionary<string, long> today = await svc.RefusalsSinceAsync(DateTimeOffset.Now);
        Assert.Equal(7, today["anjal_smtp_auth_failures_total"]);
        Assert.Equal(3, today["anjal_spam_rejected_total"]);

        var drill = new RestoreDrill { Date = "2026-10-01", Passed = true, By = "arun@dashc.example", RecordedAt = DateTimeOffset.UtcNow };
        await svc.RecordDrillAsync(drill);
        Assert.Equal(new DateTime(2027, 4, 1), MailboxService.NextDrillDue(await svc.DrillsAsync()));
    }

    [Fact]
    public void TheDiskForecast_FollowsTheGrowthOfTheRecord()
    {
        var days = new List<DiskDay>();
        for (int i = 0; i < 30; i++)
        {
            days.Add(new DiskDay { Date = new DateTime(2026, 9, 1).AddDays(i).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), Total = 100_000_000_000, Used = 40_000_000_000 + (i * 100_000_000L), Evidence = i * 10_000_000L });
        }
        // 100 MB a day, 57.1 GB left: 571 days, about 19 months.
        Assert.Equal(19, MailboxService.MonthsUntilFull(days, new ServiceCapacity(100_000_000_000, 42_900_000_000, 0, 0, 0, 0)));
        Assert.Null(MailboxService.MonthsUntilFull(days.Take(3).ToList(), new ServiceCapacity(100_000_000_000, 40_000_000_000, 0, 0, 0, 0)));
        Assert.Equal(290_000_000, MailboxService.GrowthOver(days, 30, d => d.Evidence));
    }

    private static string Token(string html) => TokenRegex().Match(html).Groups[1].Value;

    private static async Task<HttpResponseMessage> Post(HttpClient c, string path, string token, params (string Key, string Value)[] fields)
    {
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        form.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        using var content = new FormUrlEncodedContent(form);
        return await c.PostAsync(new Uri(path, UriKind.Relative), content);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    [GeneratedRegex("<span class=\"hval\">[0-9.]+ MB</span>", RegexOptions.CultureInvariant)]
    private static partial Regex MbRegex();

    private async Task<HttpClient> SignedInAsync(string who)
    {
        var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(this.baseAddress + "/") };
        this.clients.Add(c);
        using HttpResponseMessage signedIn = await Post(c, "auth/login", Token(await c.GetStringAsync(new Uri("sign-in", UriKind.Relative))), ("address", who + "@dashc.example"), ("password", "correct horse battery"));
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.DoesNotContain("sign-in", signedIn.Headers.Location!.ToString(), StringComparison.Ordinal);
        return c;
    }

    private async Task DeliverAsync(string raw)
    {
        DeliveryResult r = await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "sender@example.com",
            EnvelopeTo = ArunRecipient,
            RawBytes = Encoding.UTF8.GetBytes(raw),
        });
        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
    }
}
