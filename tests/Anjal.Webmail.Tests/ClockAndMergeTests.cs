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
/// Owner, 8 Oct 2026: (1) every mail in a list shows its time and, under it, the day; (2) the
/// clock is 24-hour unless the organisation chooses 12-hour, each person may choose their own,
/// and someone who never chose follows the organisation, including later changes; (3) Send one
/// each is only for the people their organisation's administrator gives it to.
/// </summary>
public sealed partial class ClockAndMergeTests : IAsyncLifetime, IDisposable
{
    // Its own organisation, so the clock it sets is no other test's.
    private const string Slug = "clocktest";
    private static readonly string[] ArunRecipient = { "arun@clock.example" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-clock-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly List<HttpClient> clients = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private string baseAddress = string.Empty;
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
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = Slug, DisplayName = "Clock Hospital", PostmasterMailbox = "arun@clock.example" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "clock.example" });
        await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "arun", Domain = "clock.example", DisplayName = "Arun", PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000) });
        this.meera = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "meera", Domain = "clock.example", DisplayName = "Meera", PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000) });
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
    public void TheClock_Writes24Or12Hour_AndTheDayUnderTheTime()
    {
        DateTimeOffset now = new(2026, 10, 8, 9, 0, 0, TimeSpan.FromHours(5.5));
        ZonedClock c = ZonedClock.For("Asia/Kolkata", null);
        Assert.Equal("14:30", c.Time(new DateTimeOffset(2026, 10, 7, 14, 30, 0, TimeSpan.FromHours(5.5))));
        Assert.Equal("2:30 pm", c.WithHours("12").Time(new DateTimeOffset(2026, 10, 7, 14, 30, 0, TimeSpan.FromHours(5.5))));
        Assert.Equal("12:05 am", c.WithHours("12").Time(new DateTimeOffset(2026, 10, 7, 0, 5, 0, TimeSpan.FromHours(5.5))));
        Assert.Equal("Today", c.ListDay(now.AddHours(-1), now));
        Assert.Equal("Yesterday", c.ListDay(now.AddDays(-1), now));
        Assert.Equal("Tue", c.ListDay(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.FromHours(5.5)), now));
        Assert.Equal("30 Sep", c.ListDay(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.FromHours(5.5)), now));
        Assert.Equal("24", Clocks.For(string.Empty, "an-organisation-never-seen"));
    }

    [Fact]
    public async Task APersonFollowsTheOrganisationsClock_UntilTheyChooseTheirOwn()
    {
        await this.DeliverAsync("From: Board <board@x.example>\r\nTo: arun@clock.example\r\nSubject: Minutes\r\nDate: Wed, 07 Oct 2026 14:30:00 +0530\r\n\r\nx\r\n");
        HttpClient c = await this.SignedInAsync("arun");
        Assert.Matches(TimeInList("\\d{2}:\\d{2}"), await c.GetStringAsync(new Uri("folder/INBOX", UriKind.Relative)));

        // The organisation chooses the 12-hour clock: everyone who never chose follows at once.
        string token = Token(await c.GetStringAsync(new Uri("org/branding", UriKind.Relative)));
        using (var branding = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" }, { new StringContent("12"), "clock" }, { new StringContent("anjal"), "theme" }, { new StringContent("en"), "language" } })
        {
            using HttpResponseMessage r = await c.PostAsync(new Uri("org/branding", UriKind.Relative), branding);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        }
        string list = await c.GetStringAsync(new Uri("folder/INBOX", UriKind.Relative));
        Assert.Matches(TimeInList("\\d{1,2}:\\d{2} (am|pm)"), list);
        Assert.Contains("data-hours=\"12\"", list, StringComparison.Ordinal);
        Assert.Contains("Same as Clock Hospital (12-hour)", await c.GetStringAsync(new Uri("settings/language", UriKind.Relative)), StringComparison.Ordinal);

        // The person chooses 24-hour for themselves; the organisation's later choice no longer applies to them.
        token = Token(await c.GetStringAsync(new Uri("settings/language", UriKind.Relative)));
        using (HttpResponseMessage r = await Post(c, "settings/language", token, ("language", "en"), ("timeZone", "Asia/Kolkata"), ("dateFormat", "language"), ("weekStart", "monday"), ("clock", "24")))
        {
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        }
        Assert.Matches(TimeInList("\\d{2}:\\d{2}"), await c.GetStringAsync(new Uri("folder/INBOX", UriKind.Relative)));
        Assert.Equal("24", (await this.app.Services.GetRequiredService<MailboxService>().GetMailSettingsAsync((await this.store.GetMailboxAsync("arun", "clock.example"))!.Id)).Clock);
    }

    [Fact]
    public async Task SendOneEach_IsOnlyForPeopleTheOrganisationAllows()
    {
        HttpClient m = await this.SignedInAsync("meera");
        string compose = await m.GetStringAsync(new Uri("compose", UriKind.Relative));
        Assert.DoesNotContain("name=\"merge\"", compose, StringComparison.Ordinal);
        using (HttpResponseMessage preview = await m.GetAsync(new Uri("api/merge-preview?to=a%40x.example", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
        }
        using (var form = new MultipartFormDataContent { { new StringContent(Token(compose)), "__RequestVerificationToken" }, { new StringContent("a@x.example, b@x.example"), "to" }, { new StringContent("Hello {First name}"), "subject" }, { new StringContent("x"), "body" }, { new StringContent("1"), "merge" } })
        {
            using HttpResponseMessage sent = await m.PostAsync(new Uri("compose", UriKind.Relative), form);
            Assert.Contains("not%20turned%20on%20for%20you", sent.Headers.Location!.ToString(), StringComparison.Ordinal);
        }

        // The administrator allows it for Meera: her compose offers it, and it is logged.
        HttpClient a = await this.SignedInAsync("arun");
        string people = await a.GetStringAsync(new Uri("org/people?sel=" + this.meera.Id, UriKind.Relative));
        Assert.Contains("Allow Send one each", people, StringComparison.Ordinal);
        using (HttpResponseMessage r = await Post(a, "org/people/" + this.meera.Id + "/merge", Token(people), ("value", "on")))
        {
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        }
        Assert.Contains("Stop Send one each", await a.GetStringAsync(new Uri("org/people?sel=" + this.meera.Id, UriKind.Relative)), StringComparison.Ordinal);
        Assert.Contains("name=\"merge\"", await m.GetStringAsync(new Uri("compose", UriKind.Relative)), StringComparison.Ordinal);
        Assert.Contains(await this.store.ListAuditAsync(50), e => e.Action == "org.person.merge.on" && e.Subject == "meera@clock.example");
    }

    private static Regex TimeInList(string time) => new("<time[^>]*><span class=\"tt\">" + time + "</span><small class=\"td\">[^<]+</small></time>", RegexOptions.CultureInvariant);

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

    private async Task<HttpClient> SignedInAsync(string who)
    {
        var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(this.baseAddress + "/") };
        this.clients.Add(c);
        using HttpResponseMessage signedIn = await Post(c, "auth/login", Token(await c.GetStringAsync(new Uri("sign-in", UriKind.Relative))), ("address", who + "@clock.example"), ("password", "correct horse battery"));
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
            RawBytes = System.Text.Encoding.UTF8.GetBytes(raw),
        });
        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
    }
}
