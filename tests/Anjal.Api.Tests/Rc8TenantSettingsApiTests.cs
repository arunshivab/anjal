using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anjal.Store;

namespace Anjal.Api.Tests;

/// <summary>
/// v1.0.0-rc.8: a tenant's evidence retention and postmaster mailbox are set
/// through the API, validated, and never reset by an update that omits them.
/// </summary>
public sealed class Rc8TenantSettingsApiTests : System.IDisposable
{
    private readonly InMemoryMessageStore store = new();
    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-rc8-tenant-" + System.Guid.NewGuid().ToString("N"));
    private readonly ApiServer server;
    private readonly System.Threading.CancellationTokenSource cts = new();
    private readonly System.Threading.Tasks.Task serverTask;
    private readonly HttpClient client;

    public Rc8TenantSettingsApiTests()
    {
        int port = FreePort.Next();
        this.server = new ApiServer(new ApiOptions { BindAddress = IPAddress.Loopback, Port = port, BearerToken = "t" }, this.store, null, this.store, new Anjal.Mailbox.MaildirStore(this.root, "test"));
        this.serverTask = this.server.StartAsync(this.cts.Token);
        System.Threading.Thread.Sleep(100);
        this.client = new HttpClient { BaseAddress = new System.Uri($"http://127.0.0.1:{port}/") };
        this.client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "t");
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
            System.IO.Directory.Delete(this.root, recursive: true);
        }
    }

    private async Task<HttpResponseMessage> PostAsync(object body) => await this.client.PostAsJsonAsync("api/tenants", body);

    [Fact]
    public async Task RetentionAndPostmaster_AreSet_AndAnUpdateThatOmitsThemKeepsThem()
    {
        using (HttpResponseMessage set = await this.PostAsync(new { slug = "imagiqa", displayName = "imagiQa", enabled = true, evidenceRetentionDays = 2000, postmasterMailbox = "arun@anjal.co.in" }))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        }
        using (HttpResponseMessage update = await this.PostAsync(new { slug = "imagiqa", displayName = "imagiQa Healthcare", enabled = true }))
        {
            using JsonDocument doc = JsonDocument.Parse(await update.Content.ReadAsStringAsync());
            Assert.Equal(2000, doc.RootElement.GetProperty("evidenceRetentionDays").GetInt32());
            Assert.Equal("arun@anjal.co.in", doc.RootElement.GetProperty("postmasterMailbox").GetString());
        }
        TenantRow t = (await this.store.GetTenantAsync("imagiqa"))!;
        Assert.Equal((2000, "arun@anjal.co.in", "imagiQa Healthcare"), (t.EvidenceRetentionDays, t.PostmasterMailbox!, t.DisplayName));

        using (HttpResponseMessage cleared = await this.PostAsync(new { slug = "imagiqa", displayName = "imagiQa", enabled = true, postmasterMailbox = string.Empty }))
        {
            Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        }
        Assert.Null((await this.store.GetTenantAsync("imagiqa"))!.PostmasterMailbox);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(36501, null)]
    [InlineData(null, "not-an-address")]
    public async Task InvalidValues_AreRefused(int? days, string? postmaster)
    {
        using HttpResponseMessage r = await this.PostAsync(new { slug = "imagiqa", displayName = "x", enabled = true, evidenceRetentionDays = days, postmasterMailbox = postmaster });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}
