using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Anjal.Api.Dto;
using Anjal.Store;

namespace Anjal.Api.Tests;

public class DkimKeyApiTests : System.IDisposable
{
    private static int nextPort = 39400;

    private readonly InMemoryMessageStore store;
    private readonly ApiServer server;
    private readonly System.Threading.CancellationTokenSource cts;
    private readonly System.Threading.Tasks.Task serverTask;
    private readonly HttpClient client;
    private readonly string token;
    private readonly string validPem;
    private bool disposed;

    public DkimKeyApiTests()
    {
        int port = System.Threading.Interlocked.Increment(ref nextPort);
        this.token = "test-token-" + System.Guid.NewGuid().ToString("N");
        this.store = new InMemoryMessageStore();
        this.cts = new System.Threading.CancellationTokenSource();
        this.server = new ApiServer(new ApiOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = port,
            BearerToken = this.token,
        }, this.store);
        this.serverTask = this.server.StartAsync(this.cts.Token);
        System.Threading.Thread.Sleep(100);

        this.client = new HttpClient { BaseAddress = new System.Uri($"http://127.0.0.1:{port}/") };
        this.client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", this.token);

        // Generate one valid RSA key to use throughout.
        using RSA rsa = RSA.Create(2048);
        this.validPem = rsa.ExportPkcs8PrivateKeyPem();
    }

    public void Dispose()
    {
        this.Dispose(true);
        System.GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (this.disposed) return;
        this.disposed = true;
        if (disposing)
        {
            this.client.Dispose();
            this.cts.Cancel();
            try { this.serverTask.Wait(2000); }
#pragma warning disable CA1031
            catch (System.Exception) { }
#pragma warning restore CA1031
            this.server.Dispose();
            this.cts.Dispose();
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Post_CreatesKey()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/dkim-keys", new DkimKeyRequest
        {
            Domain = "mail.lipi.in",
            Selector = "default",
            PrivateKeyPem = this.validPem,
        }, ApiJson.Options);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        DkimKeyResponse? body = await res.Content.ReadFromJsonAsync<DkimKeyResponse>(ApiJson.Options);
        Assert.NotNull(body);
        Assert.Equal("mail.lipi.in", body!.Domain);
        Assert.Equal("default", body.Selector);
    }

    [Fact]
    public async System.Threading.Tasks.Task Post_DoesNotReturnPrivateKey()
    {
        // The response DTO has no PrivateKeyPem field. Just verify the raw response body too.
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/dkim-keys", new DkimKeyRequest
        {
            Domain = "mail.lipi.in",
            Selector = "s",
            PrivateKeyPem = this.validPem,
        }, ApiJson.Options);

        string raw = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("BEGIN", raw, System.StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", raw, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task Post_RejectsInvalidPem()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/dkim-keys", new DkimKeyRequest
        {
            Domain = "mail.lipi.in",
            Selector = "default",
            PrivateKeyPem = "-----BEGIN PRIVATE KEY-----\nGARBAGE\n-----END PRIVATE KEY-----",
        }, ApiJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Post_RejectsNonPemKey()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/dkim-keys", new DkimKeyRequest
        {
            Domain = "mail.lipi.in",
            Selector = "default",
            PrivateKeyPem = "not-a-pem",
        }, ApiJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Post_MissingDomain_Returns400()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/dkim-keys", new DkimKeyRequest
        {
            Selector = "default",
            PrivateKeyPem = this.validPem,
        }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Post_MissingSelector_Returns400()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/dkim-keys", new DkimKeyRequest
        {
            Domain = "x.test",
            PrivateKeyPem = this.validPem,
        }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Get_ListsKeys()
    {
        await this.client.PostAsJsonAsync("api/dkim-keys", new DkimKeyRequest
        {
            Domain = "a.test",
            Selector = "s",
            PrivateKeyPem = this.validPem,
        }, ApiJson.Options);

        HttpResponseMessage res = await this.client.GetAsync(new System.Uri("api/dkim-keys", System.UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var list = await res.Content.ReadFromJsonAsync<System.Collections.Generic.List<DkimKeyResponse>>(ApiJson.Options);
        Assert.NotNull(list);
        Assert.Single(list!);
    }

    [Fact]
    public async System.Threading.Tasks.Task Get_DoesNotReturnPrivateKeys()
    {
        await this.client.PostAsJsonAsync("api/dkim-keys", new DkimKeyRequest
        {
            Domain = "a.test",
            Selector = "s",
            PrivateKeyPem = this.validPem,
        }, ApiJson.Options);

        HttpResponseMessage res = await this.client.GetAsync(new System.Uri("api/dkim-keys", System.UriKind.Relative));
        string raw = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("BEGIN", raw, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task Delete_RemovesKey()
    {
        await this.client.PostAsJsonAsync("api/dkim-keys", new DkimKeyRequest
        {
            Domain = "gone.test",
            Selector = "s",
            PrivateKeyPem = this.validPem,
        }, ApiJson.Options);

        HttpResponseMessage del = await this.client.DeleteAsync(new System.Uri("api/dkim-keys/gone.test", System.UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        HttpResponseMessage del2 = await this.client.DeleteAsync(new System.Uri("api/dkim-keys/gone.test", System.UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, del2.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Delete_RequiresAuth()
    {
        using var noauth = new HttpClient { BaseAddress = this.client.BaseAddress };
        HttpResponseMessage res = await noauth.DeleteAsync(new System.Uri("api/dkim-keys/x.test", System.UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
