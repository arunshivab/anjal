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
/// v1.0.0-rc.8, SPEC-08 R-13: the message page tells the user the original is
/// kept, with its fingerprint; a reconstructed copy is never called "as
/// received"; deleting says the original stays for the retention period.
/// </summary>
public sealed class Rc8EvidenceDisplayTests : IAsyncLifetime, IDisposable
{
    private static readonly string[] Arun = { "arun@anjal.co.in" };
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc8-display-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private WebApplication app = null!;
    private HttpClient client = null!;
    private MailboxRow mailbox = null!;
    private MaildirStore maildir = null!;

    public void Dispose() => this.client?.Dispose();

    public async Task InitializeAsync()
    {
        var maildir = this.maildir = new MaildirStore(Path.Combine(this.root, "mail"), "test");
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        this.mailbox = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "arun", Domain = "anjal.co.in", PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000) });

        var recorder = new EvidenceRecorder(this.store, new EvidenceVault(Path.Combine(this.root, "evidence")));
        var sink = new MailboxSink(this.store, maildir);
        foreach ((string subject, bool keep) in new[] { ("Kept", true), ("Old", false) })
        {
            byte[] raw = System.Text.Encoding.ASCII.GetBytes($"From: s@x.test\r\nSubject: {subject}\r\n\r\nbody\r\n");
            Guid? evidence = keep ? await recorder.RecordInboundAsync(new InboundEvidence { RawBytes = raw, EnvelopeFrom = "s@x.test", EnvelopeTo = Arun, ReceivedAt = DateTimeOffset.UtcNow }) : null;
            await sink.DeliverAsync(new DeliveryContext { EnvelopeFrom = "s@x.test", EnvelopeTo = Arun, RawBytes = raw, EvidenceId = evidence });
        }

        this.app = Program.CreateApp(Array.Empty<string>(), this.store, maildir, "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
        string address = this.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        this.client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(address + "/") };
        string html = await (await this.client.GetAsync("sign-in")).Content.ReadAsStringAsync();
        HttpResponseMessage login = await this.client.PostAsync("auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = TokenRegex.Match(html).Groups[1].Value,
            ["address"] = "arun@anjal.co.in",
            ["password"] = "correct horse battery",
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
    }

    public async Task DisposeAsync()
    {
        await this.app.StopAsync();
        await this.app.DisposeAsync();
        if (Directory.Exists(this.root))
        {
            foreach (string f in Directory.GetFiles(this.root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }
            Directory.Delete(this.root, recursive: true);
        }
    }

    private async Task<string> PageAsync(string subject)
    {
        MessageRow row = this.store.MailboxMessages.Single(m => m.Subject == subject);
        return WebUtility.HtmlDecode(await this.client.GetStringAsync($"message/{row.Id}"));
    }

    [Fact]
    public async Task AKeptOriginal_IsShownWithItsFingerprint_AndDeletingSaysItStaysForThreeYears()
    {
        EvidenceRow evidence = (await this.store.GetEvidenceAsync(this.store.MailboxMessages.Single(m => m.Subject == "Kept").EvidenceId!.Value))!;
        MessageRow row = this.store.MailboxMessages.Single(m => m.Subject == "Kept");
        // The "Delete permanently" choice shows in Trash: move the file and the index, as the webmail does.
        FolderRow inbox = await this.store.EnsureFolderAsync(this.mailbox.Id, FolderRow.Inbox);
        FolderRow trash = await this.store.EnsureFolderAsync(this.mailbox.Id, "Trash");
        string moved = this.maildir.Move("imagiqa", this.mailbox.Address, inbox.Name, row.MaildirFile, trash.Name)!;
        await this.store.MoveMessageAsync(row.Id, trash.Id, moved);

        string html = await this.PageAsync("Kept");
        Assert.Contains("Original kept exactly as received", html, StringComparison.Ordinal);
        Assert.Contains($"title=\"SHA-256 {evidence.Sha256}\"", html, StringComparison.Ordinal);
        Assert.Contains(evidence.Sha256[..12] + "…", html, StringComparison.Ordinal);
        Assert.Contains("Its original is kept as evidence for 3 years.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReconstructedOriginal_IsNeverCalledAsReceived()
    {
        MessageRow old = this.store.MailboxMessages.Single(m => m.Subject == "Old");
        var id = Guid.NewGuid();
        await this.store.InsertEvidenceAsync(new EvidenceRow { Id = id, Direction = EvidenceRow.In, CapturedAt = DateTimeOffset.UtcNow, EnvelopeFrom = "s@x.test", EnvelopeTo = Arun, SizeBytes = 1, Sha256 = new string('a', 64), Path = "x.eml", Reconstructed = true, Outcome = "accepted" });
        await this.store.SetMessageEvidenceAsync(old.Id, id);

        string html = await this.PageAsync("Old");
        Assert.Contains("Original reconstructed", html, StringComparison.Ordinal);
        Assert.DoesNotContain("exactly as received", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MailWithNoKeptOriginal_ShowsNoSuchLine()
    {
        string html = await this.PageAsync("Old");
        Assert.DoesNotContain("Original kept", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Original reconstructed", html, StringComparison.Ordinal);
    }
}
