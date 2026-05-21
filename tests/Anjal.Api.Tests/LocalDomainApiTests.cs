using System.Net;
using System.Net.Http.Json;
using Anjal.Api.Dto;
using Anjal.Store;

namespace Anjal.Api.Tests;

public class LocalDomainApiTests : System.IDisposable
{
    private static int nextPort = 39700;

    private readonly InMemoryMessageStore store;
    private readonly ApiServer server;
    private readonly System.Threading.CancellationTokenSource cts;
    private readonly System.Threading.Tasks.Task serverTask;
    private readonly HttpClient client;
    private readonly string token;
    private bool disposed;

    public LocalDomainApiTests()
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
    public async System.Threading.Tasks.Task Post_RegistersDomain()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/local-domains", new LocalDomainRequest
        {
            Domain = "hospital-a.test",
        }, ApiJson.Options);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        LocalDomainResponse? body = await res.Content.ReadFromJsonAsync<LocalDomainResponse>(ApiJson.Options);
        Assert.NotNull(body);
        Assert.Equal("hospital-a.test", body!.Domain);
    }

    [Fact]
    public async System.Threading.Tasks.Task Post_MissingDomain_Returns400()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/local-domains", new LocalDomainRequest(), ApiJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Get_ListsDomains()
    {
        await this.client.PostAsJsonAsync("api/local-domains", new LocalDomainRequest { Domain = "a.test" }, ApiJson.Options);
        await this.client.PostAsJsonAsync("api/local-domains", new LocalDomainRequest { Domain = "b.test" }, ApiJson.Options);

        HttpResponseMessage res = await this.client.GetAsync(new System.Uri("api/local-domains", System.UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var list = await res.Content.ReadFromJsonAsync<System.Collections.Generic.List<LocalDomainResponse>>(ApiJson.Options);
        Assert.NotNull(list);
        Assert.Equal(2, list!.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task Delete_RemovesDomain()
    {
        await this.client.PostAsJsonAsync("api/local-domains", new LocalDomainRequest { Domain = "gone.test" }, ApiJson.Options);

        HttpResponseMessage del = await this.client.DeleteAsync(new System.Uri("api/local-domains/gone.test", System.UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        HttpResponseMessage del2 = await this.client.DeleteAsync(new System.Uri("api/local-domains/gone.test", System.UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, del2.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Post_Idempotent_SameDomainTwice()
    {
        await this.client.PostAsJsonAsync("api/local-domains", new LocalDomainRequest { Domain = "x.test" }, ApiJson.Options);
        HttpResponseMessage second = await this.client.PostAsJsonAsync("api/local-domains", new LocalDomainRequest { Domain = "x.test" }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        HttpResponseMessage list = await this.client.GetAsync(new System.Uri("api/local-domains", System.UriKind.Relative));
        var rows = await list.Content.ReadFromJsonAsync<System.Collections.Generic.List<LocalDomainResponse>>(ApiJson.Options);
        Assert.Single(rows!);
    }
}
