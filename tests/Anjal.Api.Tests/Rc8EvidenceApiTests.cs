using System.Net;
using System.Text;
using System.Text.Json;
using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Api.Tests;

/// <summary>
/// v1.0.0-rc.8, SPEC-08 R-13 and R-02/R-03: the evidence and maintenance
/// endpoints over real HTTP. Every evidence read is audited; an altered file
/// is never returned; corrections are dry runs unless ?apply=true.
/// </summary>
public sealed class Rc8EvidenceApiTests : System.IDisposable
{
    private static readonly string[] Recipient = { "arun@anjal.co.in" };
    private readonly InMemoryMessageStore store = new();
    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-rc8-api-" + System.Guid.NewGuid().ToString("N"));
    private readonly ApiServer server;
    private readonly System.Threading.CancellationTokenSource cts = new();
    private readonly System.Threading.Tasks.Task serverTask;
    private readonly HttpClient client;

    public Rc8EvidenceApiTests()
    {
        int port = FreePort.Next();
        string token = "t-" + System.Guid.NewGuid().ToString("N");
        this.server = new ApiServer(new ApiOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = port,
            BearerToken = token,
            EvidenceRoot = System.IO.Path.Combine(this.root, "evidence"),
            HostName = "mail.anjal.co.in",
        }, this.store, null, this.store, new MaildirStore(System.IO.Path.Combine(this.root, "mail"), "test"));
        this.serverTask = this.server.StartAsync(this.cts.Token);
        System.Threading.Thread.Sleep(100);
        this.client = new HttpClient { BaseAddress = new System.Uri($"http://127.0.0.1:{port}/") };
        this.client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    public void Dispose()
    {
        this.client.Dispose();
        this.cts.Cancel();
        try
        {
            this.serverTask.Wait(2000);
        }
#pragma warning disable CA1031
        catch (System.Exception)
        {
        }
#pragma warning restore CA1031
        this.server.Dispose();
        this.cts.Dispose();
        if (System.IO.Directory.Exists(this.root))
        {
            foreach (string f in System.IO.Directory.GetFiles(this.root, "*", System.IO.SearchOption.AllDirectories))
            {
                System.IO.File.SetAttributes(f, System.IO.FileAttributes.Normal);
            }
            System.IO.Directory.Delete(this.root, recursive: true);
        }
    }

    private async Task<(System.Guid Id, byte[] Bytes)> KeepAsync()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("From: doctor@example.org\r\nSubject: Report\r\n\r\nBody.\r\n");
        var recorder = new EvidenceRecorder(this.store, new EvidenceVault(System.IO.Path.Combine(this.root, "evidence")));
        System.Guid id = await recorder.RecordInboundAsync(new InboundEvidence { RawBytes = bytes, EnvelopeFrom = "doctor@example.org", EnvelopeTo = Recipient, ReceivedAt = System.DateTimeOffset.UtcNow });
        await recorder.CompleteInboundAsync(id, accepted: true);
        return (id, bytes);
    }

    [Fact]
    public async Task EvidenceDetailsAndOriginal_AreReturned_AndEveryReadIsAudited()
    {
        (System.Guid id, byte[] bytes) = await this.KeepAsync();

        using HttpResponseMessage details = await this.client.GetAsync($"api/evidence/{id}");
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await details.Content.ReadAsStringAsync());
        Assert.Equal(EvidenceVault.Sha256Hex(bytes), doc.RootElement.GetProperty("sha256").GetString());
        Assert.Equal("accepted", doc.RootElement.GetProperty("outcome").GetString());

        using HttpResponseMessage raw = await this.client.GetAsync($"api/evidence/{id}/raw");
        Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
        Assert.Equal(bytes, await raw.Content.ReadAsByteArrayAsync());
        Assert.Equal(EvidenceVault.Sha256Hex(bytes), raw.Headers.GetValues("X-Anjal-Evidence-Sha256").Single());
        Assert.Equal("message/rfc822", raw.Content.Headers.ContentType!.MediaType);

        IReadOnlyList<AuditEvent> audit = await this.store.ListAuditAsync(50);
        Assert.Contains(audit, a => a.Action == "READ evidence details" && a.Subject == id.ToString());
        Assert.Contains(audit, a => a.Action == "READ evidence raw" && a.Subject == id.ToString() && a.Detail == "sha256 verified");
    }

    [Fact]
    public async Task AnAlteredOriginal_IsNeverReturned_AndVerifySaysSo()
    {
        (System.Guid id, _) = await this.KeepAsync();
        string file = System.IO.Path.Combine(this.root, "evidence", (await this.store.GetEvidenceAsync(id))!.Path);
        System.IO.File.SetAttributes(file, System.IO.FileAttributes.Normal);
        if (!System.OperatingSystem.IsWindows())
        {
            System.IO.File.SetUnixFileMode(file, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite);
        }
        await System.IO.File.WriteAllTextAsync(file, "tampered");

        using HttpResponseMessage raw = await this.client.GetAsync($"api/evidence/{id}/raw");
        Assert.Equal(HttpStatusCode.Conflict, raw.StatusCode);
        Assert.DoesNotContain("tampered", await raw.Content.ReadAsStringAsync(), System.StringComparison.Ordinal);

        using HttpResponseMessage verify = await this.client.GetAsync($"api/evidence/{id}/verify");
        using JsonDocument doc = JsonDocument.Parse(await verify.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("intact").GetBoolean());
    }

    [Fact]
    public async Task Maintenance_IsADryRunUnlessApplyIsTrue_AndTrustedSendersCanBeListedAndRemoved()
    {
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa" });
        MailboxRow mailbox = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "arun", Domain = "anjal.co.in" });
        await this.store.AddTrustedSenderAsync(mailbox.Id, "noreply@spamhaus.org");

        using HttpResponseMessage dry = await this.client.PostAsync("api/maintenance/reconstruct-evidence", null);
        using JsonDocument d = JsonDocument.Parse(await dry.Content.ReadAsStringAsync());
        Assert.False(d.RootElement.GetProperty("applied").GetBoolean());

        using HttpResponseMessage labels = await this.client.PostAsync("api/maintenance/transport-labels", null);
        using JsonDocument l = JsonDocument.Parse(await labels.Content.ReadAsStringAsync());
        Assert.False(l.RootElement.GetProperty("applied").GetBoolean());

        using HttpResponseMessage list = await this.client.GetAsync("api/maintenance/trusted-senders");
        Assert.Contains("noreply@spamhaus.org", await list.Content.ReadAsStringAsync(), System.StringComparison.Ordinal);
        using HttpResponseMessage removed = await this.client.DeleteAsync("api/maintenance/trusted-senders?mailbox=arun@anjal.co.in&sender=noreply@spamhaus.org");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Empty(await this.store.ListTrustedSendersAsync(mailbox.Id));
    }
}
