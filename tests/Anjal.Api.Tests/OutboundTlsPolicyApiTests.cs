using System.Net;
using System.Net.Http.Json;
using Anjal.Api.Dto;
using Anjal.Store;

namespace Anjal.Api.Tests;

public class OutboundTlsPolicyApiTests : System.IDisposable
{
    private static int nextPort = 39200;

    private readonly InMemoryMessageStore store;
    private readonly ApiServer server;
    private readonly System.Threading.CancellationTokenSource cts;
    private readonly System.Threading.Tasks.Task serverTask;
    private readonly HttpClient client;
    private readonly string token;
    private bool disposed;

    public OutboundTlsPolicyApiTests()
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
    public async System.Threading.Tasks.Task Post_CreatesPolicy()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/outbound-tls-policies", new OutboundTlsPolicyRequest
        {
            Domain = "gmail.com",
            Mode = "required",
        }, ApiJson.Options);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        OutboundTlsPolicyResponse? body = await res.Content.ReadFromJsonAsync<OutboundTlsPolicyResponse>(ApiJson.Options);
        Assert.NotNull(body);
        Assert.Equal("gmail.com", body!.Domain);
        Assert.Equal("required", body.Mode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Post_LowercasesDomain()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/outbound-tls-policies", new OutboundTlsPolicyRequest
        {
            Domain = "Gmail.COM",
            Mode = "required",
        }, ApiJson.Options);

        OutboundTlsPolicyResponse? body = await res.Content.ReadFromJsonAsync<OutboundTlsPolicyResponse>(ApiJson.Options);
        Assert.NotNull(body);
        Assert.Equal("gmail.com", body!.Domain);
    }

    [Fact]
    public async System.Threading.Tasks.Task Post_Upsert_UpdatesExisting()
    {
        await this.client.PostAsJsonAsync("api/outbound-tls-policies", new OutboundTlsPolicyRequest
        {
            Domain = "x.test",
            Mode = "opportunistic",
        }, ApiJson.Options);

        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/outbound-tls-policies", new OutboundTlsPolicyRequest
        {
            Domain = "x.test",
            Mode = "required",
        }, ApiJson.Options);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        OutboundTlsPolicyResponse? body = await res.Content.ReadFromJsonAsync<OutboundTlsPolicyResponse>(ApiJson.Options);
        Assert.Equal("required", body!.Mode);

        // Only one policy total.
        HttpResponseMessage list = await this.client.GetAsync(new System.Uri("api/outbound-tls-policies", System.UriKind.Relative));
        var all = await list.Content.ReadFromJsonAsync<System.Collections.Generic.List<OutboundTlsPolicyResponse>>(ApiJson.Options);
        Assert.Single(all!);
    }

    [Fact]
    public async System.Threading.Tasks.Task Post_MissingDomain_Returns400()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/outbound-tls-policies", new OutboundTlsPolicyRequest
        {
            Mode = "required",
        }, ApiJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Post_InvalidMode_Returns400()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/outbound-tls-policies", new OutboundTlsPolicyRequest
        {
            Domain = "x.test",
            Mode = "bogus",
        }, ApiJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Get_ReturnsList()
    {
        await this.client.PostAsJsonAsync("api/outbound-tls-policies", new OutboundTlsPolicyRequest
        {
            Domain = "a.test",
            Mode = "required",
        }, ApiJson.Options);
        await this.client.PostAsJsonAsync("api/outbound-tls-policies", new OutboundTlsPolicyRequest
        {
            Domain = "b.test",
            Mode = "disabled",
        }, ApiJson.Options);

        HttpResponseMessage res = await this.client.GetAsync(new System.Uri("api/outbound-tls-policies", System.UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var list = await res.Content.ReadFromJsonAsync<System.Collections.Generic.List<OutboundTlsPolicyResponse>>(ApiJson.Options);
        Assert.NotNull(list);
        Assert.Equal(2, list!.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task Delete_RemovesPolicy()
    {
        await this.client.PostAsJsonAsync("api/outbound-tls-policies", new OutboundTlsPolicyRequest
        {
            Domain = "gone.test",
            Mode = "required",
        }, ApiJson.Options);

        HttpResponseMessage del = await this.client.DeleteAsync(new System.Uri("api/outbound-tls-policies/gone.test", System.UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        HttpResponseMessage del2 = await this.client.DeleteAsync(new System.Uri("api/outbound-tls-policies/gone.test", System.UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, del2.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Delete_RequiresAuth()
    {
        using var noauth = new HttpClient { BaseAddress = this.client.BaseAddress };
        HttpResponseMessage res = await noauth.DeleteAsync(new System.Uri("api/outbound-tls-policies/x.test", System.UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
