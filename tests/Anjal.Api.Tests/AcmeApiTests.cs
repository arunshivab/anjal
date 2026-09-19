using System.Net;
using System.Net.Http.Json;
using Anjal.Api.Dto;
using Anjal.Store;

namespace Anjal.Api.Tests;

public sealed class AcmeApiTests : System.IDisposable
{
    private readonly string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-acme-api-" + System.Guid.NewGuid().ToString("N"));
    private readonly ApiServer server;
    private readonly System.Threading.CancellationTokenSource cts = new();
    private readonly System.Threading.Tasks.Task serverTask;
    private readonly HttpClient client;

    public AcmeApiTests()
    {
        int port = FreePort.Next();
        this.server = new ApiServer(new ApiOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = port,
            BearerToken = string.Empty,
            AcmeDirectory = this.dir,
        }, new InMemoryMessageStore());
        this.serverTask = this.server.StartAsync(this.cts.Token);
        System.Threading.Thread.Sleep(100);
        this.client = new HttpClient { BaseAddress = new System.Uri($"http://127.0.0.1:{port}/") };
    }

    public void Dispose()
    {
        this.client.Dispose();
        this.cts.Cancel();
        try { this.serverTask.Wait(2000); }
#pragma warning disable CA1031
        catch (System.Exception) { }
#pragma warning restore CA1031
        this.server.Dispose();
        this.cts.Dispose();
        if (System.IO.Directory.Exists(this.dir))
        {
            System.IO.Directory.Delete(this.dir, recursive: true);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Status_WithoutCertificate_ReportsNone()
    {
        AcmeStatusResponse? status = await this.client.GetFromJsonAsync<AcmeStatusResponse>("api/acme", ApiJson.Options);
        Assert.NotNull(status);
        Assert.False(status!.HasCertificate);
        Assert.Null(status.NotAfter);
        Assert.False(status.RenewalPending);
        Assert.Equal(System.IO.Path.GetFullPath(this.dir), status.Directory);
    }

    [Fact]
    public async System.Threading.Tasks.Task Renew_WritesMarker_AndStatusShowsPending()
    {
        HttpResponseMessage res = await this.client.PostAsync("api/acme/renew", null);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        AcmeRenewResponse? body = await res.Content.ReadFromJsonAsync<AcmeRenewResponse>(ApiJson.Options);
        Assert.True(body!.Requested);
        Assert.True(System.IO.File.Exists(body.Marker));

        AcmeStatusResponse? status = await this.client.GetFromJsonAsync<AcmeStatusResponse>("api/acme", ApiJson.Options);
        Assert.True(status!.RenewalPending);
    }

    [Fact]
    public async System.Threading.Tasks.Task WrongMethods_Are404()
    {
        HttpResponseMessage get = await this.client.GetAsync("api/acme/renew");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        HttpResponseMessage post = await this.client.PostAsync("api/acme", null);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
    }
}

public class AcmeApiDisabledTests
{
    [Fact]
    public async System.Threading.Tasks.Task WithoutAcmeDirectory_RoutesAre404()
    {
        int port = FreePort.Next();
        using var cts = new System.Threading.CancellationTokenSource();
        using var server = new ApiServer(new ApiOptions { BindAddress = IPAddress.Loopback, Port = port, BearerToken = string.Empty }, new InMemoryMessageStore());
        System.Threading.Tasks.Task task = server.StartAsync(cts.Token);
        await System.Threading.Tasks.Task.Delay(100);
        using var client = new HttpClient { BaseAddress = new System.Uri($"http://127.0.0.1:{port}/") };
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("api/acme")).StatusCode);
        cts.Cancel();
        try { await task; }
        catch (System.OperationCanceledException) { }
    }
}
