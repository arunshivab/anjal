using System.Net;
using System.Net.Http.Json;
using Anjal.Api.Dto;
using Anjal.Store;

namespace Anjal.Api.Tests;

public sealed class HealthApiTests : System.IDisposable
{
    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-health-" + System.Guid.NewGuid().ToString("N"));
    private readonly ApiServer server;
    private readonly System.Threading.CancellationTokenSource cts = new();
    private readonly System.Threading.Tasks.Task serverTask;
    private readonly HttpClient anon;
    private readonly HttpClient authed;

    public HealthApiTests()
    {
        int port = FreePort.Next();
        this.server = new ApiServer(new ApiOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = port,
            BearerToken = "secret",
            MaildirRoot = System.IO.Path.Combine(this.root, "mail"),
            AcmeDirectory = System.IO.Path.Combine(this.root, "acme"),
        }, new InMemoryMessageStore());
        this.serverTask = this.server.StartAsync(this.cts.Token);
        System.Threading.Thread.Sleep(100);
        this.anon = new HttpClient { BaseAddress = new System.Uri($"http://127.0.0.1:{port}/") };
        this.authed = new HttpClient { BaseAddress = new System.Uri($"http://127.0.0.1:{port}/") };
        this.authed.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret");
    }

    public void Dispose()
    {
        this.anon.Dispose();
        this.authed.Dispose();
        this.cts.Cancel();
        try { this.serverTask.Wait(2000); }
#pragma warning disable CA1031
        catch (System.Exception) { }
#pragma warning restore CA1031
        this.server.Dispose();
        this.cts.Dispose();
        if (System.IO.Directory.Exists(this.root))
        {
            System.IO.Directory.Delete(this.root, recursive: true);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Healthz_IsUnauthenticated_ReportsComponents()
    {
        HttpResponseMessage res = await this.anon.GetAsync("healthz");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        HealthResponse? body = await res.Content.ReadFromJsonAsync<HealthResponse>(ApiJson.Options);
        Assert.NotNull(body);
        Assert.Equal("degraded", body!.Status); // ACME configured but no certificate yet
        Assert.Contains(body.Components, c => c.Name == "store" && c.Status == "ok");
        Assert.Contains(body.Components, c => c.Name == "maildir" && c.Status == "ok");
        Assert.Contains(body.Components, c => c.Name == "tls" && c.Status == "degraded");
        Assert.True(System.IO.Directory.Exists(System.IO.Path.Combine(this.root, "mail")));
        Assert.Empty(System.IO.Directory.GetFiles(System.IO.Path.Combine(this.root, "mail"), ".healthz-*"));

        HttpResponseMessage post = await this.anon.PostAsync("healthz", null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Metrics_RequiresAuth_RendersPrometheus()
    {
        HttpResponseMessage unauth = await this.anon.GetAsync("metrics");
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

        Anjal.Smtp.Counters.Increment("anjal_test_metrics_probe_total");
        HttpResponseMessage res = await this.authed.GetAsync("metrics");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.StartsWith("text/plain", res.Content.Headers.ContentType!.MediaType, System.StringComparison.Ordinal);
        string text = await res.Content.ReadAsStringAsync();
        Assert.Contains("# TYPE anjal_test_metrics_probe_total counter", text, System.StringComparison.Ordinal);
        Assert.Contains("# TYPE anjal_uptime_seconds gauge", text, System.StringComparison.Ordinal);
    }
}

public class HealthNoTlsTests
{
    [Fact]
    public async System.Threading.Tasks.Task WithoutAcmeOrMaildir_IsOk()
    {
        int port = FreePort.Next();
        using var cts = new System.Threading.CancellationTokenSource();
        using var server = new ApiServer(new ApiOptions { BindAddress = IPAddress.Loopback, Port = port, BearerToken = string.Empty }, new InMemoryMessageStore());
        System.Threading.Tasks.Task task = server.StartAsync(cts.Token);
        await System.Threading.Tasks.Task.Delay(100);
        using var client = new HttpClient { BaseAddress = new System.Uri($"http://127.0.0.1:{port}/") };
        HealthResponse? body = await client.GetFromJsonAsync<HealthResponse>("healthz", ApiJson.Options);
        Assert.Equal("ok", body!.Status);
        cts.Cancel();
        try { await task; }
        catch (System.OperationCanceledException) { }
    }
}
