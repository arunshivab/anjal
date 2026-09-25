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
    private readonly int port;

    public AcmeApiTests()
    {
        int port = FreePort.Next();
        this.port = port;
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

    /// <summary>
    /// DEF-053: <c>curl -X POST</c> with no body sends neither Content-Length
    /// nor chunked encoding. The listener answers 411 Length Required - and
    /// then handed the request to the API anyway, so the renewal was requested
    /// while the caller was told it had failed (measured on the production
    /// server, 26 Sep 2026). A caller who retries burns Let's Encrypt's limit
    /// of five duplicate certificates a week. A request answered 411 must
    /// change nothing.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task PostWithoutContentLength_Gets411_AndChangesNothing()
    {
        string statusLine;
        using (var tcp = new System.Net.Sockets.TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, this.port);
            using System.Net.Sockets.NetworkStream stream = tcp.GetStream();
            byte[] request = System.Text.Encoding.ASCII.GetBytes(
                "POST /api/acme/renew HTTP/1.1\r\nHost: 127.0.0.1\r\nUser-Agent: curl/8.5.0\r\nAccept: */*\r\n\r\n");
            await stream.WriteAsync(request);
            using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.ASCII);
            statusLine = await reader.ReadLineAsync() ?? string.Empty;
        }

        Assert.Contains("411", statusLine, System.StringComparison.Ordinal);

        // The handler ran after the 411 had been sent; give it every chance.
        await System.Threading.Tasks.Task.Delay(1500);
        Assert.False(System.IO.File.Exists(System.IO.Path.Combine(this.dir, "renew.request")));
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
