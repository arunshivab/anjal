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
/// rc.15, item 41 (owner, 7 Oct 2026): every word on every screen comes from the owner's word
/// list, so that each language, once checked, covers the whole webmail. Every screen is opened -
/// as a person, an organisation's administrator and an operator, and before signing in - and
/// every link found on them is followed once; any word a screen asked for that is not in the
/// list fails the test, by name.
/// </summary>
[Collection("Operators")]
public sealed partial class EveryScreenSpeaksTests : IAsyncLifetime, IDisposable
{
    private static readonly string[] ArunRecipient = { "arun@anjal.co.in" };

    private static readonly string[] Screens =
    {
        "folder/INBOX", "folder/Sent", "folder/Drafts", "folder/Junk", "folder/Trash", "folder/Archive", "folder/Scheduled",
        "folder/INBOX?show=unread", "folder/INBOX?view=focus", "folder/INBOX?view=list", "outbox", "no-reply",
        "compose", "search?q=report", "contacts", "contacts?new=1",
        "dashboard", "dashboard?a=12m", "dashboard/org",
        "settings/appearance", "settings/language", "settings/mail", "settings/security", "settings/security/two-step",
        "settings/rules", "settings/categories", "settings/senders", "settings/help",
        "org/people", "org/people?invite=1", "org/signin", "org/domains", "org/shared", "org/shared?new=1", "org/retention",
        "org/sending", "org/apps", "org/apps?new=1", "org/log", "org/branding",
        "ops", "ops/orgs", "ops/orgs?new=1", "ops/limits", "ops/health",
    };

    private static readonly string[] Outside = { "sign-in", "sign-in/forgot", "signed-out", "unavailable" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-words-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly List<HttpClient> clients = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private string baseAddress = string.Empty;
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
        Environment.SetEnvironmentVariable("ANJAL_OPERATORS", "arun@anjal.co.in");
        this.maildir = new MaildirStore(this.root, "test");
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "Imagiqa", PostmasterMailbox = "arun@anjal.co.in" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
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
        Environment.SetEnvironmentVariable("ANJAL_OPERATORS", this.operatorsBefore);
        await this.app.StopAsync();
        await this.app.DisposeAsync();
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    [Fact]
    public async Task EveryWordOnEveryScreen_IsInTheWordList()
    {
        Assert.NotNull(Lexicon.Listed);
        await this.DeliverAsync("From: Accounts <accounts@supplier.example>\r\nTo: arun@anjal.co.in\r\nSubject: Invoice report 114\r\n\r\nThe invoice is attached.\r\n");
        await this.DeliverAsync("From: Lottery <win@prize.example>\r\nTo: arun@anjal.co.in\r\nSubject: You have WON a prize\r\n\r\nSend your bank details now!\r\n");
        Lexicon.ClearUnlisted();

        HttpClient outside = this.Client();
        foreach (string screen in Outside)
        {
            await outside.GetAsync(new Uri(screen, UriKind.Relative));
        }

        HttpClient c = this.Client();
        string token = TokenRegex().Match(await c.GetStringAsync("sign-in")).Groups[1].Value;
        using (var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["address"] = "arun",
            ["password"] = "correct horse battery",
        }))
        {
            using HttpResponseMessage signedIn = await c.PostAsync(new Uri("auth/login", UriKind.Relative), form);
            Assert.DoesNotContain("/sign-in", signedIn.RequestMessage!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var next = new Queue<string>(Screens);
        while (next.Count > 0 && seen.Count < 220)
        {
            string url = next.Dequeue();
            if (!seen.Add(url))
            {
                continue;
            }
            using HttpResponseMessage res = await c.GetAsync(new Uri(url, UriKind.Relative));
            if (res.Content.Headers.ContentType?.MediaType != "text/html")
            {
                continue;
            }
            string html = await res.Content.ReadAsStringAsync();
            foreach (Match link in HrefRegex().Matches(html))
            {
                string href = WebUtility.HtmlDecode(link.Groups[1].Value).TrimStart('/');
                if (href.Length > 0 && !href.StartsWith("auth/", StringComparison.Ordinal) && !href.Contains("raw", StringComparison.Ordinal)
                    && !href.Contains("download", StringComparison.Ordinal) && !href.Contains("export", StringComparison.Ordinal) && !seen.Contains(href))
                {
                    next.Enqueue(href);
                }
            }
        }

        IReadOnlyCollection<string> missed = Lexicon.Unlisted;
        Assert.True(missed.Count == 0, $"Screens ({seen.Count} opened) asked for {missed.Count} word(s) not in the word list:\n  " + string.Join("\n  ", missed.OrderBy(w => w, StringComparer.Ordinal)));
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    [GeneratedRegex("href=\"(/[^\"#]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex HrefRegex();

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

    private HttpClient Client()
    {
        var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true }) { BaseAddress = new Uri(this.baseAddress + "/") };
        this.clients.Add(c);
        return c;
    }
}
