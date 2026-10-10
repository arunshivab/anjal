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
/// rc.15, found by the screen check (item 47, 7 Oct 2026): a search started without choosing a
/// folder made every link on its results - opening a result, the next page, Clear search - point
/// at a folder called "folder", so opening a result emptied the list. The links keep the folder
/// searched.
/// </summary>
public sealed partial class SearchLinksTests : IAsyncLifetime, IDisposable
{
    private static readonly string[] ArunRecipient = { "arun@anjal.co.in" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-search-" + Guid.NewGuid().ToString("N"));
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
        await this.app.StopAsync();
        await this.app.DisposeAsync();
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    [Fact]
    public async Task ASearchWithoutAFolderChosen_KeepsTheFolderInEveryLink()
    {
        await this.DeliverAsync("From: Accounts <accounts@supplier.example>\r\nTo: arun@anjal.co.in\r\nSubject: Invoice report 114\r\n\r\nThe invoice is attached.\r\n");
        HttpClient c = await this.SignedInAsync();

        string results = await c.GetStringAsync(new Uri("search?q=report", UriKind.Relative));
        Assert.DoesNotContain("scope=folder", results, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/folder/folder\"", results, StringComparison.Ordinal);
        Assert.Contains("href=\"/folder/INBOX\"", results, StringComparison.Ordinal);

        // Opening the result keeps it in the list beside it.
        string open = WebUtility.HtmlDecode(OpenRegex().Match(results).Groups[1].Value);
        Assert.StartsWith("/search?q=report&scope=INBOX&", open, StringComparison.Ordinal);
        string opened = await c.GetStringAsync(new Uri(open.TrimStart('/'), UriKind.Relative));
        Assert.Contains("Invoice report 114", opened, StringComparison.Ordinal);
        Assert.DoesNotContain("scope=folder", opened, StringComparison.Ordinal);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    [GeneratedRegex("href=\"(/search\\?[^\"]*open=[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex OpenRegex();

    private async Task<HttpClient> SignedInAsync()
    {
        var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true }) { BaseAddress = new Uri(this.baseAddress + "/") };
        this.clients.Add(c);
        string token = TokenRegex().Match(await c.GetStringAsync(new Uri("sign-in", UriKind.Relative))).Groups[1].Value;
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["address"] = "arun",
            ["password"] = "correct horse battery",
        });
        using HttpResponseMessage signedIn = await c.PostAsync(new Uri("auth/login", UriKind.Relative), form);
        Assert.DoesNotContain("/sign-in", signedIn.RequestMessage!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
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
