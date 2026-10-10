using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Anjal.Webmail.Tests;

/// <summary>
/// rc.15 through the real pipeline: the person's, the administrator's and
/// the operator's dashboards with their own periods, the spam-score bar
/// opening its messages, and offline mail - its setting, its list, its
/// kept pages that do not mark mail read, and its worker.
/// </summary>
[Collection("Operators")]
public sealed class Rc15FlowTests : IAsyncLifetime, IDisposable
{
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly string[] ArunRecipient = { "arun@anjal.co.in" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc15e-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly List<HttpClient> clients = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private string baseAddress = string.Empty;
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
        this.maildir = new MaildirStore(this.root, "test");
        this.tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "Imagiqa", PostmasterMailbox = "arun@anjal.co.in" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.tenant.Id, Domain = "anjal.co.in" });
        foreach ((string local, string name) in new[] { ("arun", "Arun Shiva B"), ("meera.iyer", "Meera Iyer") })
        {
            await this.store.UpsertMailboxAsync(new MailboxRow
            {
                TenantId = this.tenant.Id,
                LocalPart = local,
                Domain = "anjal.co.in",
                DisplayName = name,
                PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
            });
        }
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
    public async Task PersonsDashboard_OpensOnToday_EachGraphWithItsOwnPeriod()
    {
        await this.DeliverAsync("From: Accounts <accounts@supplier.example>\r\nTo: arun@anjal.co.in\r\nSubject: Invoice 114\r\n\r\nThe invoice is attached.\r\n");
        HttpClient c = this.Client();
        await SignInAsync(c, "arun");
        string page = await c.GetStringAsync("dashboard");
        Assert.Contains("Your dashboard", page, StringComparison.Ordinal);
        Assert.Contains("Received, sent and Junk", page, StringComparison.Ordinal);
        Assert.Contains("Activity, by hour", page, StringComparison.Ordinal);
        Assert.Contains("Messages by spam score", page, StringComparison.Ordinal);
        Assert.Contains("Unread 1", page, StringComparison.Ordinal);
        Assert.Contains("Invoice 114", page, StringComparison.Ordinal);
        Assert.Contains("Needs attention", page, StringComparison.Ordinal);
        Assert.Contains("Who wrote to you most", page, StringComparison.Ordinal);
        Assert.Contains("Mailbox health", page, StringComparison.Ordinal);
        Assert.Contains("Your safety", page, StringComparison.Ordinal);
        Assert.Contains("Junk from 5", page, StringComparison.Ordinal);
        Assert.Contains(">Score<", page, StringComparison.Ordinal);
        Assert.Contains(">Messages<", page, StringComparison.Ordinal);
        // Changing one graph's period keeps the others.
        Assert.Contains("href=\"/dashboard?a=7d&amp;s=12m#d-activity\"", await c.GetStringAsync("dashboard?s=12m"), StringComparison.Ordinal);
        string month = await c.GetStringAsync("dashboard?a=12m");
        Assert.Contains("Activity, by month", month, StringComparison.Ordinal);
        // An administrator may switch to the organisation's figures; a member may not.
        Assert.Contains("href=\"/dashboard/org\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrganisationDashboard_IsForItsAdministrators_TotalsOnly()
    {
        HttpClient member = this.Client();
        await SignInAsync(member, "meera.iyer");
        Assert.Contains("Only an administrator of your organisation can open this.", await member.GetStringAsync("dashboard/org"), StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/dashboard/org\"", await member.GetStringAsync("dashboard"), StringComparison.Ordinal);

        HttpClient admin = this.Client();
        await SignInAsync(admin, "arun");
        string page = await admin.GetStringAsync("dashboard/org");
        Assert.Contains("Imagiqa dashboard", page, StringComparison.Ordinal);
        Assert.Contains("Totals only: no one&#x27;s messages are shown.", page, StringComparison.Ordinal);
        Assert.Contains("Who sends as anjal.co.in", page, StringComparison.Ordinal);
        Assert.Contains("have two-step sign-in on", page, StringComparison.Ordinal);
        Assert.Contains("Figures per person", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OperatorsOverview_IsTheConsolesFirstPage()
    {
        string? before = Environment.GetEnvironmentVariable("ANJAL_OPERATORS");
        try
        {
            Environment.SetEnvironmentVariable("ANJAL_OPERATORS", "arun@anjal.co.in");
            HttpClient op = this.Client();
            await SignInAsync(op, "arun");
            string page = await op.GetStringAsync("ops");
            Assert.Contains("Anjal service overview", page, StringComparison.Ordinal);
            Assert.Contains("Totals per organisation only: never people or mail.", page, StringComparison.Ordinal);
            Assert.Contains("Server capacity", page, StringComparison.Ordinal);
            Assert.Contains("Organisations by mail received", page, StringComparison.Ordinal);
            Assert.Contains("Business", page, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANJAL_OPERATORS", before);
        }
    }

    [Fact]
    public async Task OfflineMail_OffByDefault_ThenKeepsTheListOnly_NeverTheMail()
    {
        MessageRow m = await this.DeliverAsync("From: Lab <lab@hospital.example>\r\nTo: arun@anjal.co.in\r\nSubject: Results ready\r\n\r\nYour results are ready.\r\n");
        HttpClient c = this.Client();
        await SignInAsync(c, "arun");
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("offline/list")).StatusCode);
        Assert.Contains("data-offline-mail=\"off\"", await c.GetStringAsync("folder/INBOX"), StringComparison.Ordinal);

        string settings = await c.GetStringAsync("settings/mail");
        Assert.Contains("Keep my mail list on my devices, to look at without a connection", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("data-offline-lock", settings, StringComparison.Ordinal);
        HttpResponseMessage on = await Post(c, "settings/offline", Token(settings), ("on", "1"));
        Assert.Equal("/settings/mail?saved=offline", on.Headers.Location!.ToString());
        Assert.Contains("data-offline-mail=\"on\"", await c.GetStringAsync("folder/INBOX"), StringComparison.Ordinal);
        Assert.Contains("data-offline-lock", await c.GetStringAsync("settings/mail"), StringComparison.Ordinal);

        using JsonDocument list = JsonDocument.Parse(await c.GetStringAsync("offline/list"));
        Assert.Contains(list.RootElement.GetProperty("messages").EnumerateArray(), e => e.GetProperty("id").GetGuid() == m.Id && e.GetProperty("s").GetString() == "Results ready");
        Assert.Equal(7, list.RootElement.GetProperty("expireDays").GetInt32());
        Assert.False(string.IsNullOrEmpty(list.RootElement.GetProperty("session").GetString()));

        // DES-11 D5: only the list - the mail itself is never handed out for keeping.
        Assert.DoesNotContain("Your results are ready.", list.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.NotEqual(HttpStatusCode.OK, (await c.GetAsync($"offline/message/{m.Id}")).StatusCode);
        Assert.False((await this.store.GetMessageByIdAsync(m.Id))!.Seen);

        HttpResponseMessage sw = await c.GetAsync("sw.js");
        Assert.Equal(HttpStatusCode.OK, sw.StatusCode);
        string worker = await sw.Content.ReadAsStringAsync();
        Assert.Contains("AES-GCM", worker, StringComparison.Ordinal);
        Assert.Contains("PBKDF2", worker, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("offline.js")).StatusCode);

        HttpResponseMessage off = await Post(c, "settings/offline", Token(await c.GetStringAsync("settings/mail")));
        Assert.Equal("/settings/mail?saved=offlineoff", off.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("offline/list")).StatusCode);
    }

    [Fact]
    public async Task OfflineMail_TheOrganisationMayKeepSubjectsOff_AndThePagesCarryTheWorkersWords()
    {
        await this.DeliverAsync("From: Lab <lab@hospital.example>\r\nTo: arun@anjal.co.in\r\nSubject: Biopsy results\r\n\r\nPrivate.\r\n");
        Anjal.Webmail.Services.MailboxService svc = this.app.Services.GetRequiredService<Anjal.Webmail.Services.MailboxService>();
        Anjal.Webmail.Services.SignInPolicy policy = await svc.SignInPolicyForAsync((await this.store.GetMailboxAsync("arun", "anjal.co.in"))!.Id);
        policy.OfflineSubjects = false;
        Assert.Null(await svc.SaveSignInPolicyAsync(this.tenant.Id, policy));

        HttpClient c = this.Client();
        await SignInAsync(c, "arun");
        await Post(c, "settings/offline", Token(await c.GetStringAsync("settings/mail")), ("on", "1"));
        string list = await c.GetStringAsync("offline/list");
        Assert.DoesNotContain("Biopsy", list, StringComparison.Ordinal);
        Assert.Contains("\"w\":\"Lab\"", list, StringComparison.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(list);
        Assert.False(doc.RootElement.GetProperty("subjects").GetBoolean());

        string inbox = await c.GetStringAsync("folder/INBOX");
        Assert.Contains("data-offline-words=", inbox, StringComparison.Ordinal);
        Assert.Contains("Five wrong tries remove the list from this device.", WebUtility.HtmlDecode(inbox), StringComparison.Ordinal);
    }

    /// <summary>
    /// Owner, 9 Oct 2026 (B): the live channel gives the Inbox's state at once, again as soon as
    /// mail comes, and ends after its time so the browser asks again; signed out, it is refused.
    /// </summary>
    [Fact]
    public async Task LiveChannel_SaysAtOnceThatMailCame_ThenEnds()
    {
        TimeSpan life = Program.LiveStreamLife;
        TimeSpan step = Program.LiveStreamStep;
        Program.LiveStreamLife = TimeSpan.FromSeconds(4);
        Program.LiveStreamStep = TimeSpan.FromMilliseconds(100);
        try
        {
            HttpClient outside = this.Client();
            using (HttpResponseMessage refused = await outside.GetAsync("api/events"))
            {
                Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
            }

            HttpClient c = this.Client();
            await SignInAsync(c, "arun");
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/events");
            using HttpResponseMessage res = await c.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal("text/event-stream", res.Content.Headers.ContentType!.MediaType);
            using var reader = new StreamReader(await res.Content.ReadAsStreamAsync());
            string first = await NextEventAsync(reader, "state");
            await this.DeliverAsync("From: Lab <lab@hospital.example>\r\nTo: arun@anjal.co.in\r\nSubject: Live\r\n\r\nNew.\r\n");
            string second = await NextEventAsync(reader, "state");
            using JsonDocument a = JsonDocument.Parse(first);
            using JsonDocument b = JsonDocument.Parse(second);
            Assert.True(b.RootElement.GetProperty("newest").GetInt64() > a.RootElement.GetProperty("newest").GetInt64());
            Assert.Equal(a.RootElement.GetProperty("unread").GetInt64() + 1, b.RootElement.GetProperty("unread").GetInt64());
            await NextEventAsync(reader, "end");
        }
        finally
        {
            Program.LiveStreamLife = life;
            Program.LiveStreamStep = step;
        }
    }

    private static async Task<string> NextEventAsync(StreamReader reader, string name)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? kind = null;
        while (true)
        {
            string? line = await reader.ReadLineAsync(stop.Token);
            Assert.NotNull(line);
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                kind = line[7..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && kind == name)
            {
                return line[6..];
            }
        }
    }

    [Fact]
    public async Task OfflineMail_NeverOnASharedComputer()
    {
        HttpClient c = this.Client();
        await SignInAsync(c, "arun", ("device", "shared"));
        string settings = await c.GetStringAsync("settings/mail");
        Assert.Contains("Mail is never kept on a shared computer.", settings, StringComparison.Ordinal);
        await Post(c, "settings/offline", Token(settings), ("on", "1"));
        Assert.Contains("data-offline-mail=\"off\"", await c.GetStringAsync("folder/INBOX"), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("offline/list")).StatusCode);
    }

    [Fact]
    public async Task SpamScoreBar_OpensItsMessages()
    {
        await this.DeliverAsync("From: Someone <someone@example.net>\r\nTo: arun@anjal.co.in\r\nSubject: Plain hello\r\n\r\nHello.\r\n");
        HttpClient c = this.Client();
        await SignInAsync(c, "arun");
        MessageRow row = this.store.MailboxMessages[^1];
        row.SpamChecked = true;
        row.SpamScore = 3;
        string page = await c.GetStringAsync("search?scope=all&score=3");
        Assert.Contains("Plain hello", page, StringComparison.Ordinal);
        Assert.Contains("spam score 3", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Plain hello", await c.GetStringAsync("search?scope=all&score=9"), StringComparison.Ordinal);
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

    private static async Task SignInAsync(HttpClient c, string user, params (string Key, string Value)[] extra)
    {
        string token = Token(await c.GetStringAsync("sign-in"));
        HttpResponseMessage res = await Post(c, "auth/login", token, new[] { ("address", user), ("password", "correct horse battery") }.Concat(extra).ToArray());
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
    }

    private async Task<MessageRow> DeliverAsync(string raw)
    {
        DeliveryResult r = await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "sender@example.com",
            EnvelopeTo = ArunRecipient,
            RawBytes = System.Text.Encoding.UTF8.GetBytes(raw),
        });
        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
        return this.store.MailboxMessages[^1];
    }

    private HttpClient Client()
    {
        var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(this.baseAddress + "/") };
        this.clients.Add(c);
        return c;
    }
}
