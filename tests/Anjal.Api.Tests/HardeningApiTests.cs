using System.Net;
using System.Net.Http.Json;
using Anjal.Api.Dto;
using Anjal.Store;

namespace Anjal.Api.Tests;

public sealed class HardeningApiTests : System.IDisposable
{
    private readonly InMemoryMessageStore store = new();
    private readonly ApiServer server;
    private readonly System.Threading.CancellationTokenSource cts = new();
    private readonly HttpClient client;

    public HardeningApiTests()
    {
        int port = FreePort.Next();
        this.server = new ApiServer(new ApiOptions { BindAddress = IPAddress.Loopback, Port = port, BearerToken = "secret" }, this.store);
        _ = this.server.StartAsync(this.cts.Token);
        System.Threading.Thread.Sleep(100);
        this.client = new HttpClient { BaseAddress = new System.Uri($"http://127.0.0.1:{port}/") };
        this.client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret");
    }

    public void Dispose()
    {
        this.client.Dispose();
        this.cts.Cancel();
        this.server.Dispose();
        this.cts.Dispose();
    }

    [Fact]
    public async System.Threading.Tasks.Task ChangesAreAudited_ReadsAreNot_AndBodiesAreNeverStored()
    {
        await this.client.GetAsync("api/routing-rules");
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/routing-rules", new RoutingRuleRequest
        {
            LocalPart = "reports",
            WebhookUrl = "https://app.test/hook",
            WebhookSecret = "very-secret-hmac-key",
        }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // The entry is written just after the response goes out, so the
        // caller never waits on it; give it a moment to land.
        IReadOnlyList<AuditEvent> events = System.Array.Empty<AuditEvent>();
        for (int i = 0; i < 40 && events.Count == 0; i++)
        {
            await System.Threading.Tasks.Task.Delay(50);
            events = await this.store.ListAuditAsync(10);
        }
        AuditEvent e = Assert.Single(events);
        Assert.Equal("api", e.Actor);
        Assert.Equal("POST /api/routing-rules", e.Action);
        Assert.Equal("status 200", e.Detail);
        Assert.Equal("127.0.0.1", e.RemoteAddress);
        Assert.DoesNotContain("very-secret", e.Action + e.Subject + e.Detail, System.StringComparison.Ordinal);

        string listing = await this.client.GetStringAsync("api/audit?limit=5");
        Assert.Contains("POST /api/routing-rules", listing, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://app.test/hook")]
    [InlineData("https://10.0.0.5/hook")]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://user:pass@app.test/hook")]
    public async System.Threading.Tasks.Task UnsafeWebhookUrls_AreRefused(string url)
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/routing-rules", new RoutingRuleRequest
        {
            LocalPart = "reports",
            WebhookUrl = url,
            WebhookSecret = "abc",
        }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(await this.store.ListRoutingRulesAsync());
    }
}

public class ScrubbedErrorTests
{
    public class BrokenStore : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
            throw new System.InvalidOperationException("Npgsql: could not connect to db-internal.example:5432 as anjal_admin");
    }

    [Fact]
    public async System.Threading.Tasks.Task AnInternalError_ReturnsAReference_NotTheExceptionText()
    {
        int port = FreePort.Next();
        IMessageStore broken = System.Reflection.DispatchProxy.Create<IMessageStore, BrokenStore>();
        using var cts = new System.Threading.CancellationTokenSource();
        using var server = new ApiServer(new ApiOptions { BindAddress = IPAddress.Loopback, Port = port, BearerToken = "t" }, broken);
        _ = server.StartAsync(cts.Token);
        await System.Threading.Tasks.Task.Delay(100);
        using var client = new HttpClient { BaseAddress = new System.Uri($"http://127.0.0.1:{port}/") };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "t");

        HttpResponseMessage res = await client.GetAsync("api/routing-rules");
        string body = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Contains("Reference", body, System.StringComparison.Ordinal);
        Assert.DoesNotContain("db-internal", body, System.StringComparison.Ordinal);
        Assert.DoesNotContain("anjal_admin", body, System.StringComparison.Ordinal);
        cts.Cancel();
    }
}
