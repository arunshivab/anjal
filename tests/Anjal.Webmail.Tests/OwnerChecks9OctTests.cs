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
/// Owner, 9 Oct 2026: (1) a dashboard number that carries its days (Uncategorised, Today) opens
/// its messages with the filters on the line under the search box - the filter panel never
/// opens by itself over the results; (2) every size is written the same way, with two decimals,
/// the server's disk included.
/// </summary>
public sealed partial class OwnerChecks9OctTests : IAsyncLifetime, IDisposable
{
    private const string Slug = "ninth";

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-ninth-" + Guid.NewGuid().ToString("N"));
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
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = Slug, DisplayName = "Ninth Clinic", PostmasterMailbox = "arun@ninth.example" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "ninth.example" });
        await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "arun", Domain = "ninth.example", DisplayName = "Arun", QuotaBytes = 2L * 1024 * 1024 * 1024, PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000) });
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
    public async Task ASearchWithItsDays_ShowsItsResults_WithTheFilterPanelClosed()
    {
        HttpClient c = await this.SignedInAsync();
        string today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        string page = await c.GetStringAsync(new Uri($"search?scope=all&cat=none&after={today}&before={today}", UriKind.Relative));
        Assert.Contains("<details class=\"sfilters\">", page, StringComparison.Ordinal);
        Assert.DoesNotMatch(OpenPanel(), page);
        Assert.Contains("without a category", page, StringComparison.Ordinal);
        Assert.Contains("sfunnel on", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EverySize_HasTwoDecimals_TheDiskToo()
    {
        HttpClient c = await this.SignedInAsync();
        Assert.Contains("of 2.00 GB", await c.GetStringAsync(new Uri("folder/INBOX", UriKind.Relative)), StringComparison.Ordinal);

        (IReadOnlyList<HealthTile> tiles, _) = await this.app.Services.GetRequiredService<MailboxService>().HealthAsync(this.root, DateTimeOffset.UtcNow);
        HealthTile disk = Assert.Single(tiles, t => t.Title == "Disk");
        Assert.Equal("{p}% used ({used} of {total})", disk.Text);
        Assert.Matches(TwoDecimals(), disk.Args!["used"]);
        Assert.Matches(TwoDecimals(), disk.Args!["total"]);
    }

    [GeneratedRegex("<details class=\"sfilters\" open", RegexOptions.CultureInvariant)]
    private static partial Regex OpenPanel();

    [GeneratedRegex(@"^\d+\.\d{2} (KB|MB|GB)$", RegexOptions.CultureInvariant)]
    private static partial Regex TwoDecimals();

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    private async Task<HttpClient> SignedInAsync()
    {
        var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(this.baseAddress + "/") };
        this.clients.Add(c);
        string token = TokenRegex().Match(await c.GetStringAsync(new Uri("sign-in", UriKind.Relative))).Groups[1].Value;
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["address"] = "arun@ninth.example",
            ["password"] = "correct horse battery",
        });
        using HttpResponseMessage signedIn = await c.PostAsync(new Uri("auth/login", UriKind.Relative), form);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        return c;
    }
}
