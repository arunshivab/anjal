using System.Net;
using System.Text.RegularExpressions;
using Anjal.Mailbox;
using Anjal.Mime.Tests;
using Anjal.Smtp;
using Anjal.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Anjal.Webmail.Tests;

/// <summary>
/// rc.30 (the coordinator's decision 1 on D-405, 18 Nov): every crafted mail of the hostile set delivered through the
/// real mailbox sink, then listed and opened in the real webmail - kept whole, never a server error, and its HTML
/// cleaned into a frame that runs nothing.
/// </summary>
public sealed class Rc30HostileMailTests : IAsyncLifetime, IDisposable
{
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly string[] Arun = new[] { "arun@anjal.co.in" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc30h-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private HttpClient client = null!;

    public void Dispose() => this.client?.Dispose();

    public async Task InitializeAsync()
    {
        this.maildir = new MaildirStore(this.root, "test");
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.co.in",
            DisplayName = "Arun",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        });
        this.app = Program.CreateApp(Array.Empty<string>(), this.store, this.maildir, "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
        string address = this.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        this.client = new HttpClient(handler) { BaseAddress = new Uri(address + "/") };
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

    private static readonly string[] Refused = new[] { "a file name with a right-to-left mark" };
    private static readonly Regex Srcdoc = new("srcdoc=\"([^\"]*)\"", RegexOptions.CultureInvariant);
    private static readonly Regex Tags = new("<[^>]*>?", RegexOptions.CultureInvariant);
    private static readonly Regex Runs = new("<script|<iframe|<object|<embed|\\son[a-z]+\\s*=|javascript:|<base\\b|<meta\\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    [Fact]
    public async Task EveryHostileMail_IsKept_Listed_AndOpened_WithNothingThatRuns()
    {
        await this.SignInAsync();
        var failures = new List<string>();
        foreach ((string name, byte[] raw) in HostileMail.All())
        {
            int before = this.store.MailboxMessages.Count;
            await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext
            {
                EnvelopeFrom = "lab@x.test",
                EnvelopeTo = Arun,
                RawBytes = raw,
            });
            bool kept = this.store.MailboxMessages.Count == before + 1;
            // A program attachment is refused at delivery (rc.17), whatever its name hides: a right-to-left mark
            // that makes "report\u202Efdp.exe" look like "reportexe.pdf" does not get it in.
            if (kept == Refused.Contains(name))
            {
                failures.Add(name + (kept ? ": kept, though it carries a program" : ": not kept"));
            }
            if (!kept)
            {
                continue;
            }
            MessageRow m = this.store.MailboxMessages[this.store.MailboxMessages.Count - 1];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using HttpResponseMessage page = await this.client.GetAsync($"folder/INBOX?open={m.Id}");
            string html = await page.Content.ReadAsStringAsync();
            if (page.StatusCode != HttpStatusCode.OK || !html.Contains("id=\"openmail\"", StringComparison.Ordinal))
            {
                failures.Add($"{name}: {(int)page.StatusCode}");
                continue;
            }
            foreach (Match frame in Srcdoc.Matches(html))
            {
                // The mail's own part of the frame: Anjal's head (its charset, referrer and CSP) is not the mail.
                string whole = WebUtility.HtmlDecode(frame.Groups[1].Value);
                int open = whole.IndexOf("</head><body>", StringComparison.Ordinal);
                int close = whole.LastIndexOf("</body></html>", StringComparison.Ordinal);
                string inside = open >= 0 && close > open ? whole[(open + 13)..close] : whole;
                // Only real tags can run anything: text the cleaner escaped (&lt;script&gt;) is shown as words.
                foreach (Match tag in Tags.Matches(inside))
                {
                    if (Runs.Match(tag.Value) is { Success: true } found)
                    {
                        failures.Add($"{name}: the body keeps \"{found.Value}\" in {tag.Value[..Math.Min(tag.Value.Length, 160)]}");
                        break;
                    }
                }
            }
            if (clock.Elapsed > TimeSpan.FromSeconds(10))
            {
                failures.Add($"{name}: {clock.Elapsed}");
            }
        }
        using HttpResponseMessage list = await this.client.GetAsync("folder/INBOX");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private async Task SignInAsync()
    {
        string token = Token(await (await this.client.GetAsync("sign-in")).Content.ReadAsStringAsync());
        HttpResponseMessage res = await this.PostAsync("auth/login", token, ("address", "arun@anjal.co.in"), ("password", "correct horse battery"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
    }

    private async Task<HttpResponseMessage> PostAsync(string path, string token, params (string Key, string Value)[] fields)
    {
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        foreach ((string k, string v) in fields)
        {
            form.Add(new KeyValuePair<string, string>(k, v));
        }
        return await this.client.PostAsync(path, new FormUrlEncodedContent(form));
    }

    private static string Token(string html)
    {
        Match m = TokenRegex.Match(html);
        Assert.True(m.Success, "no antiforgery token");
        return m.Groups[1].Value;
    }
}
