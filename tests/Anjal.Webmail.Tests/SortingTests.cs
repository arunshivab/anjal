using System.Net;
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
/// Owner, 9 Oct 2026: every folder and search can be sorted - newest or oldest first, by sender
/// (recipient in Sent), by subject, biggest first, unread first, with attachments first - from a
/// Sort menu and from the headings; the order is remembered for each folder; a list not in date
/// order has no Today and Yesterday headings; names arriving encoded are put in order by name.
/// </summary>
public sealed partial class SortingTests : IAsyncLifetime, IDisposable
{
    private static readonly string[] ArunRecipient = { "arun@sort.example" };
    private static readonly string[] NewestOrder = { "Bala", "பிரியா", "Zeta Labs" };
    private static readonly string[] OldestOrder = { "Zeta Labs", "பிரியா", "Bala" };
    private static readonly string[] NameOrder = { "Bala", "Zeta Labs", "பிரியா" };
    private static readonly string[] SubjectOrder = { "பிரியா", "Zeta Labs", "Bala" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-sort-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly List<HttpClient> clients = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private string baseAddress = string.Empty;

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
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "sorting", DisplayName = "Sort Clinic", PostmasterMailbox = "arun@sort.example" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "sort.example" });
        await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "arun", Domain = "sort.example", DisplayName = "Arun", PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000) });
        this.app = Program.CreateApp(Array.Empty<string>(), this.store, this.maildir, "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
        this.baseAddress = this.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        // Oldest first: Zeta, then an encoded Tamil name (Priya), then Bala with a long letter.
        await this.DeliverAsync("From: Zeta Labs <z@x.example>\r\nTo: arun@sort.example\r\nSubject: Re: Budget\r\nDate: Mon, 05 Oct 2026 09:00:00 +0530\r\n\r\nshort\r\n");
        await this.DeliverAsync("From: =?utf-8?b?4K6q4K6/4K6w4K6/4K6v4K6+?= <p@x.example>\r\nTo: arun@sort.example\r\nSubject: Agenda\r\nDate: Tue, 06 Oct 2026 09:00:00 +0530\r\n\r\nshort\r\n");
        await this.DeliverAsync("From: Bala <b@x.example>\r\nTo: arun@sort.example\r\nSubject: Minutes\r\nDate: Wed, 07 Oct 2026 09:00:00 +0530\r\n\r\n" + new string('x', 5000) + "\r\n");
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
    public async Task AFolder_SortsEveryWay_AndRemembersIt()
    {
        HttpClient c = await this.SignedInAsync();
        string inbox = await c.GetStringAsync(new Uri("folder/INBOX", UriKind.Relative));
        Assert.Equal(NewestOrder, Names(inbox));
        Assert.Contains("mgroup-day", inbox, StringComparison.Ordinal);
        Assert.Contains("Newest first", inbox, StringComparison.Ordinal);
        Assert.Contains("lh-sorthead", inbox, StringComparison.Ordinal);

        Assert.Equal(OldestOrder, Names(await SortAsync(c, "oldest")));
        Assert.Equal(NameOrder, Names(await SortAsync(c, "sender")));
        // Re: is set aside: Agenda, Budget, Minutes.
        Assert.Equal(SubjectOrder, Names(await SortAsync(c, "subject")));
        string biggest = await SortAsync(c, "size");
        Assert.Equal("Bala", Names(biggest)[0]);
        Assert.DoesNotContain("mgroup-day", biggest, StringComparison.Ordinal);

        // Remembered for the Inbox, not for Archive; newest first forgets it.
        Assert.Equal("Bala", Names(await c.GetStringAsync(new Uri("folder/INBOX", UriKind.Relative)))[0]);
        Assert.Contains("aria-pressed=\"true\" title=\"Biggest first\"", await c.GetStringAsync(new Uri("folder/INBOX", UriKind.Relative)), StringComparison.Ordinal);
        Assert.Contains("aria-pressed=\"true\" title=\"Newest first\"", await c.GetStringAsync(new Uri("folder/Archive", UriKind.Relative)), StringComparison.Ordinal);
        Assert.Equal(NewestOrder, Names(await SortAsync(c, "newest")));
    }

    [Fact]
    public async Task SearchResults_SortToo()
    {
        HttpClient c = await this.SignedInAsync();
        string token = Token(await c.GetStringAsync(new Uri("search?q=&scope=all", UriKind.Relative)));
        using (HttpResponseMessage r = await Post(c, "settings/sort", token, ("folder", "(search)"), ("sort", "sender"), ("back", "/search?q=&scope=all")))
        {
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        }
        string results = await c.GetStringAsync(new Uri("search?q=&scope=all", UriKind.Relative));
        Assert.Equal(NameOrder, Names(results));
        Assert.DoesNotContain("mgroup-day", results, StringComparison.Ordinal);
    }

    private static string[] Names(string html) => NameRegex().Matches(html).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToArray();

    private static string Token(string html) => TokenRegex().Match(html).Groups[1].Value;

    private static async Task<HttpResponseMessage> Post(HttpClient c, string path, string token, params (string Key, string Value)[] fields)
    {
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        form.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        using var content = new FormUrlEncodedContent(form);
        return await c.PostAsync(new Uri(path, UriKind.Relative), content);
    }

    [GeneratedRegex("<span class=\"sname\">([^<]+)</span>", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    private static async Task<string> SortAsync(HttpClient c, string sort)
    {
        string token = Token(await c.GetStringAsync(new Uri("folder/INBOX", UriKind.Relative)));
        using HttpResponseMessage r = await Post(c, "settings/sort", token, ("folder", "INBOX"), ("sort", sort), ("back", "/folder/INBOX"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return await c.GetStringAsync(new Uri("folder/INBOX", UriKind.Relative));
    }

    private async Task<HttpClient> SignedInAsync()
    {
        var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(this.baseAddress + "/") };
        this.clients.Add(c);
        using HttpResponseMessage signedIn = await Post(c, "auth/login", Token(await c.GetStringAsync(new Uri("sign-in", UriKind.Relative))), ("address", "arun@sort.example"), ("password", "correct horse battery"));
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
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
