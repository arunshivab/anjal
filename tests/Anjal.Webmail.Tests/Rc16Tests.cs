using System.Net;
using System.Reflection;
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
/// rc.16 (the design review, owner 11-12 Oct 2026): search chips, Anjal's own mails, helpful empty
/// folders, the component sheet, Contacts' tabs, and the guards that keep the stylesheet, the icons
/// and the Razor honest.
/// </summary>
[Collection("Operators")]
public sealed partial class Rc16Tests : IAsyncLifetime, IDisposable
{
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly string[] Arun = new[] { "arun@anjal.co.in" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc16-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private HttpClient client = null!;
    private MailboxRow mailbox = new();

    public void Dispose() => this.client?.Dispose();

    public async Task InitializeAsync()
    {
        this.maildir = new MaildirStore(this.root, "test");
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "Imagiqa" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        this.mailbox = await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.co.in",
            DisplayName = "Arun Shiva",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        });
        await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = "meera",
            Domain = "anjal.co.in",
            DisplayName = "Meera Iyer",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("another long password", 1000),
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

    [Fact]
    public void TypedOperators_BecomeFilters_AndTheRestStaysWords()
    {
        var today = new DateOnly(2026, 10, 14);   // a Wednesday
        SearchAsk ask = SearchOperators.Parse("from:meera has:pdf discharge summary in:all", today, "monday");
        Assert.Equal("discharge summary", ask.Words);
        Assert.Equal("meera", ask.Sender);
        Assert.True(ask.Attachment);
        Assert.True(ask.AllFolders);

        SearchAsk week = SearchOperators.Parse("roster last week", today, "monday");
        Assert.Equal("roster", week.Words);
        Assert.Equal("2026-10-05", week.After);
        Assert.Equal("2026-10-11", week.Before);

        SearchAsk dated = SearchOperators.Parse("after:2026-09-01 before:2026-09-30 from:\"Lab Desk\"", today, "monday");
        Assert.Equal(string.Empty, dated.Words);
        Assert.Equal("Lab Desk", dated.Sender);
        Assert.Equal("2026-09-01", dated.After);
        Assert.Equal("2026-09-30", dated.Before);

        // Words that only look like operators stay words.
        SearchAsk plain = SearchOperators.Parse("ward 4 today's list", today, "monday");
        Assert.False(plain.HasOperators);
        Assert.Equal("ward 4 today's list", plain.Words);
    }

    [Fact]
    public async Task TheSearchPage_TurnsOperatorsIntoChips_EachRemovable()
    {
        await this.SignInAsync();
        await this.DeliverAsync("From: Lab <lab@hospital.example>\r\nSubject: Report\r\n\r\nx\r\n");
        HttpResponseMessage typed = await this.client.GetAsync(new Uri("search?q=" + Uri.EscapeDataString("from:lab has:attachment report"), UriKind.Relative));
        Assert.Equal(HttpStatusCode.Redirect, typed.StatusCode);
        string to = typed.Headers.Location!.ToString();
        Assert.Contains("q=report", to, StringComparison.Ordinal);
        Assert.Contains("sender=lab", to, StringComparison.Ordinal);
        Assert.Contains("attach=1", to, StringComparison.Ordinal);

        string page = await this.client.GetStringAsync(new Uri(to, UriKind.RelativeOrAbsolute).IsAbsoluteUri ? new Uri(to) : new Uri(to.TrimStart('/'), UriKind.Relative));
        Assert.Contains("class=\"lh-row schips\"", page, StringComparison.Ordinal);
        Assert.Contains("From: lab", page, StringComparison.Ordinal);
        Assert.Contains("Has: attachment", page, StringComparison.Ordinal);
        // Taking the sender off keeps the other filter.
        Match remove = Regex.Match(page, "From: lab<a href=\"([^\"]+)\" class=\"schip-x\"");
        Assert.True(remove.Success);
        string without = WebUtility.HtmlDecode(remove.Groups[1].Value);
        Assert.DoesNotContain("sender=", without, StringComparison.Ordinal);
        Assert.Contains("attach=1", without, StringComparison.Ordinal);
    }

    [Fact]
    public void AnjalsOwnMails_HaveOneLayout_WithTheTextBesideIt()
    {
        string text = "Arun has invited you to Imagiqa mail. Your address is meera@anjal.co.in.\r\n\r\nChoose your password here:\r\n\r\n    https://mail.anjal.co.in/invite/abc\r\n\r\nWhen: 10:44, 12 Oct 2026\r\nWhere: Chennai (10.0.0.1)\r\n\r\nImagiqa mail";
        string html = SystemMailLayout.Html(SystemMailLayout.Notice, "Your Imagiqa mail address is ready", text, "Imagiqa", "anjal.co.in");
        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
        Assert.Contains("background:#0E4B4F", html, StringComparison.Ordinal);              // the brand band
        Assert.Contains(">Your Imagiqa mail address is ready</h1>", html, StringComparison.Ordinal);
        Assert.Contains("height:44px", html, StringComparison.Ordinal);                      // one 44px button
        Assert.Contains(">Choose your password</a>", html, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(html, "display:inline-block;height:44px"));
        Assert.Contains(">When</td>", html, StringComparison.Ordinal);                       // the key/value box
        Assert.Contains("Sent by Anjal for Imagiqa &#183; anjal.co.in", html, StringComparison.Ordinal);
        string security = SystemMailLayout.Html(SystemMailLayout.Security, "New sign-in", "Someone signed in.\r\n\r\n    123456", "Imagiqa", "anjal.co.in");
        Assert.Contains("background:#F8EBC9", security, StringComparison.Ordinal);           // warning-soft band
        Assert.Contains(">123456</p>", security, StringComparison.Ordinal);                  // a code, large
        Assert.DoesNotContain("<script", SystemMailLayout.Html(SystemMailLayout.Notice, "<script>x</script>", "<b>hi</b>", "O", "h"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASystemMail_CarriesPlainTextAndTheHtmlLayout()
    {
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        Assert.Null(await svc.SendSystemMailAsync(this.mailbox, "arun@anjal.co.in", "A notice", "Hello.\r\n\r\n    https://anjal.localhost/settings", default));
        MessageRow row = this.store.MailboxMessages[^1];
        byte[]? raw = await this.maildir.ReadAsync("imagiqa", this.mailbox.Address, FolderRow.Inbox, row.MaildirFile);
        Assert.NotNull(raw);
        string text = System.Text.Encoding.UTF8.GetString(raw);
        Assert.Contains("Content-Type: multipart/alternative", text, StringComparison.Ordinal);
        Assert.Contains("Content-Type: text/plain; charset=utf-8", text, StringComparison.Ordinal);
        Assert.Contains("Content-Type: text/html; charset=utf-8", text, StringComparison.Ordinal);
        Assert.Contains("Sent by Anjal for Imagiqa", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyFolders_SayWhatGoesThere_AndTheNextStep()
    {
        await this.SignInAsync();
        string inbox = await this.client.GetStringAsync(new Uri("folder/INBOX", UriKind.Relative));
        Assert.Contains("Nothing in your Inbox.", inbox, StringComparison.Ordinal);
        Assert.Contains("href=\"/compose\">Write a message</a>", inbox, StringComparison.Ordinal);
        Assert.Contains("Trash is empty.", await this.client.GetStringAsync(new Uri("folder/Trash", UriKind.Relative)), StringComparison.Ordinal);
        string unread = await this.client.GetStringAsync(new Uri("folder/INBOX?show=unread", UriKind.Relative));
        Assert.Contains("Everything here has been read.", unread, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheComponentSheet_IsForOperatorsOnly_AndDrawsEveryFileTag()
    {
        string? before = Environment.GetEnvironmentVariable("ANJAL_OPERATORS");
        try
        {
            Environment.SetEnvironmentVariable("ANJAL_OPERATORS", "someone-else@anjal.co.in");
            await this.SignInAsync();
            string refused = await this.client.GetStringAsync(new Uri("dev/components", UriKind.Relative));
            Assert.Contains("Only an operator of this Anjal service can open this.", refused, StringComparison.Ordinal);
            Assert.DoesNotContain("class=\"devsheet\"", refused, StringComparison.Ordinal);

            Environment.SetEnvironmentVariable("ANJAL_OPERATORS", "arun@anjal.co.in");
            string sheet = await this.client.GetStringAsync(new Uri("dev/components", UriKind.Relative));
            Assert.Contains("class=\"devsheet\"", sheet, StringComparison.Ordinal);
            foreach (string kind in new[] { "pdf", "dicom", "img", "doc", "xls", "ppt", "zip", "other" })
            {
                Assert.Contains($"class=\"ftag ftag-{kind}\"", sheet, StringComparison.Ordinal);
            }
            Assert.Contains("var(--h-sm)", sheet, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANJAL_OPERATORS", before);
        }
    }

    [Fact]
    public async Task Contacts_HavePeopleGroupsAndOrganisation_AndMarkOutsiders()
    {
        await this.SignInAsync();
        string token = await this.TokenAsync("contacts?new=1");
        await this.PostAsync("contacts/save", token, ("firstName", "Ravi"), ("lastName", "Kumar"), ("address", "ravi@partner.example"));
        string people = await this.client.GetStringAsync(new Uri("contacts", UriKind.Relative));
        Assert.Contains("class=\"ctabs\"", people, StringComparison.Ordinal);
        Assert.Contains("href=\"/contacts?tab=org\"", people, StringComparison.Ordinal);
        Assert.Contains("<i class=\"eout\"> · outside</i>", people, StringComparison.Ordinal);
        string org = await this.client.GetStringAsync(new Uri("contacts?tab=org", UriKind.Relative));
        Assert.Contains("Meera Iyer", org, StringComparison.Ordinal);
        Assert.Contains("href=\"/compose?to=meera%40anjal.co.in\"", org, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/compose?to=arun%40anjal.co.in\"", org, StringComparison.Ordinal);   // not yourself
    }

    [Fact]
    public void EveryIconNamed_IsInLiPicons()
    {
        // rc.16: an unknown name renders nothing, silently; this finds it before a person does.
        var known = typeof(LiPicons.Blazor.LipiconName).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("flag", known);
        var unknown = new List<string>();
        foreach (string razor in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Anjal.Webmail", "Components"), "*.razor", SearchOption.AllDirectories))
        {
            foreach (Match m in IconName().Matches(File.ReadAllText(razor)))
            {
                if (!known.Contains(m.Groups[1].Value))
                {
                    unknown.Add(Path.GetFileName(razor) + ": " + m.Groups[1].Value);
                }
            }
        }
        Assert.True(unknown.Count == 0, "Icons not in LiPicons: " + string.Join(", ", unknown));
    }

    [Fact]
    public void NoRazorExpression_IsFollowedByAMethodCallThatWouldPrintAsText()
    {
        // The rc.16 slip: @(a ? b : c).Replace(...) prints ".Replace(...)" on the page; it must be
        // @((a ? b : c).Replace(...)).
        var slips = new List<string>();
        foreach (string razor in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Anjal.Webmail", "Components"), "*.razor", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(razor);
            int code = text.IndexOf("@code", StringComparison.Ordinal);
            if (code >= 0)
            {
                text = text[..code];
            }
            foreach (Match m in RazorSlip().Matches(text))
            {
                slips.Add(Path.GetFileName(razor) + ": " + m.Value[..Math.Min(80, m.Value.Length)]);
            }
        }
        Assert.True(slips.Count == 0, "Razor expressions followed by a method call: " + string.Join(" | ", slips));
    }

    [Fact]
    public void TheStylesheet_RepeatsNoMoreSelectorsThanItDidAfterTheClean_Up()
    {
        // Design review 4.8: one block per component. The clean-up merged what it safely could; the
        // rest stay where the cascade needs them. This count may only fall.
        string css = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anjal.Webmail", "wwwroot", "app.css"));
        List<string> repeated = RepeatedSelectors(css);
        Assert.True(repeated.Count <= RepeatedBaseline, $"{repeated.Count} selectors are declared more than once (at most {RepeatedBaseline}); add to the component's block instead. First: {string.Join(" | ", repeated.Take(10))}");
    }

    // Selectors declared more than once in the same @media (or at the top), from a simple reading of the file.
    private static List<string> RepeatedSelectors(string css)
    {
        css = Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var context = new Stack<string>();
        var buffer = new System.Text.StringBuilder();
        foreach (char ch in css)
        {
            if (ch == '{')
            {
                string head = Regex.Replace(buffer.ToString().Trim(), @"\s+", " ");
                buffer.Clear();
                if (head.StartsWith('@'))
                {
                    context.Push(head);
                    continue;
                }
                if (context.Any(c => c.StartsWith("@keyframes", StringComparison.Ordinal)))
                {
                    context.Push("frame");
                    continue;
                }
                string where = string.Join(" | ", context.Reverse());
                foreach (string sel in head.Split(','))
                {
                    string key = where + " :: " + sel.Trim();
                    seen[key] = seen.TryGetValue(key, out int n) ? n + 1 : 1;
                }
                context.Push("rule");
                continue;
            }
            if (ch == '}')
            {
                buffer.Clear();
                if (context.Count > 0)
                {
                    context.Pop();
                }
                continue;
            }
            if (ch == ';' && context.Count > 0 && context.Peek() == "rule")
            {
                buffer.Clear();
                continue;
            }
            buffer.Append(ch);
        }
        return seen.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToList();
    }

    private const int RepeatedBaseline = 298;

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private async Task<string> TokenAsync(string path)
    {
        string html = await (await this.client.GetAsync(new Uri(path, UriKind.Relative))).Content.ReadAsStringAsync();
        Match m = TokenRegex.Match(html);
        Assert.True(m.Success, "no antiforgery token on " + path);
        return m.Groups[1].Value;
    }

    private async Task<HttpResponseMessage> PostAsync(string path, string token, params (string Key, string Value)[] fields)
    {
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        foreach ((string k, string v) in fields)
        {
            form.Add(new KeyValuePair<string, string>(k, v));
        }
        using var content = new FormUrlEncodedContent(form);
        return await this.client.PostAsync(new Uri(path, UriKind.Relative), content);
    }

    private async Task SignInAsync()
    {
        string token = await this.TokenAsync("sign-in");
        HttpResponseMessage res = await this.PostAsync("auth/login", token, ("address", "arun@anjal.co.in"), ("password", "correct horse battery"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
    }

    private async Task<MessageRow> DeliverAsync(string raw)
    {
        await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "s@x.test",
            EnvelopeTo = Arun,
            RawBytes = System.Text.Encoding.UTF8.GetBytes(raw),
        });
        return this.store.MailboxMessages[this.store.MailboxMessages.Count - 1];
    }

    [GeneratedRegex("<LipiIcon Name=\"([a-z][a-z0-9-]*)\"")]
    private static partial Regex IconName();

    [GeneratedRegex(@"(?<![@\w])@\((?:[^()]|\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\))*\)\.[A-Z][A-Za-z]*\(")]
    private static partial Regex RazorSlip();
}
