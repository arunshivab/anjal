using System.Net;
using System.Net.Http.Json;
using System.Text;
using Anjal.Api.Dto;
using Anjal.Store;

namespace Anjal.Api.Tests;

public class ApiServerIntegrationTests : System.IDisposable
{

    private readonly InMemoryMessageStore store;
    private readonly ApiServer server;
    private readonly System.Threading.CancellationTokenSource cts;
    private readonly System.Threading.Tasks.Task serverTask;
    private readonly HttpClient client;
    private readonly int port;
    private readonly string token;
    private bool disposed;

    public ApiServerIntegrationTests()
    {
        this.port = FreePort.Next();
        this.token = "test-token-" + System.Guid.NewGuid().ToString("N");
        this.store = new InMemoryMessageStore();
        this.cts = new System.Threading.CancellationTokenSource();
        this.server = new ApiServer(new ApiOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = this.port,
            BearerToken = this.token,
        }, this.store);
        this.serverTask = this.server.StartAsync(this.cts.Token);
        System.Threading.Thread.Sleep(100);

        this.client = new HttpClient { BaseAddress = new System.Uri($"http://127.0.0.1:{this.port}/") };
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
        if (this.disposed)
        {
            return;
        }
        this.disposed = true;
        if (disposing)
        {
            this.client.Dispose();
            this.cts.Cancel();
            try
            {
                this.serverTask.Wait(2000);
            }
#pragma warning disable CA1031 // Test teardown - swallow shutdown errors.
            catch (System.Exception)
            {
                // Ignore - the test result already recorded.
            }
#pragma warning restore CA1031
            this.server.Dispose();
            this.cts.Dispose();
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task PostRoutingRule_ReturnsCreatedRule()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/routing-rules", new RoutingRuleRequest
        {
            LocalPart = "reports",
            WebhookUrl = "https://app.test/hook",
            WebhookSecret = "deadbeef",
        }, ApiJson.Options);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        RoutingRuleResponse? body = await res.Content.ReadFromJsonAsync<RoutingRuleResponse>(ApiJson.Options);
        Assert.NotNull(body);
        Assert.Equal("reports", body!.LocalPart);
        Assert.Equal("https://app.test/hook", body.WebhookUrl);
        Assert.NotEqual(System.Guid.Empty, body.Id);
    }

    [Fact]
    public async System.Threading.Tasks.Task PostRoutingRule_MissingLocalPart_Returns400()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/routing-rules", new RoutingRuleRequest
        {
            WebhookUrl = "https://x.test",
            WebhookSecret = "abc",
        }, ApiJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task GetRoutingRules_ReturnsList()
    {
        await this.client.PostAsJsonAsync("api/routing-rules", new RoutingRuleRequest
        {
            LocalPart = "a",
            WebhookUrl = "https://x.test",
            WebhookSecret = "s",
        }, ApiJson.Options);
        await this.client.PostAsJsonAsync("api/routing-rules", new RoutingRuleRequest
        {
            LocalPart = "b",
            WebhookUrl = "https://y.test",
            WebhookSecret = "s",
        }, ApiJson.Options);

        HttpResponseMessage res = await this.client.GetAsync(new System.Uri("api/routing-rules", System.UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        System.Collections.Generic.List<RoutingRuleResponse>? list =
            await res.Content.ReadFromJsonAsync<System.Collections.Generic.List<RoutingRuleResponse>>(ApiJson.Options);
        Assert.NotNull(list);
        Assert.Equal(2, list!.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task DeleteRoutingRule_RemovesIt()
    {
        await this.client.PostAsJsonAsync("api/routing-rules", new RoutingRuleRequest
        {
            LocalPart = "tmp",
            WebhookUrl = "https://x.test",
            WebhookSecret = "s",
        }, ApiJson.Options);

        HttpResponseMessage del = await this.client.DeleteAsync(new System.Uri("api/routing-rules/tmp", System.UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        HttpResponseMessage del2 = await this.client.DeleteAsync(new System.Uri("api/routing-rules/tmp", System.UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, del2.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task PostTagGrant_ReturnsGrant()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/tag-grants", new TagGrantRequest
        {
            LocalPart = "reports",
            Tag = "CASE-X7Y9",
            CorrelationKey = "case-7799",
            TtlSeconds = 3600,
        }, ApiJson.Options);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        TagGrantResponse? body = await res.Content.ReadFromJsonAsync<TagGrantResponse>(ApiJson.Options);
        Assert.NotNull(body);
        Assert.Equal("CASE-X7Y9", body!.Tag);
        Assert.True(body.ExpiresAt > body.CreatedAt);
    }

    [Fact]
    public async System.Threading.Tasks.Task PostTagGrant_ZeroTtl_Returns400()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/tag-grants", new TagGrantRequest
        {
            LocalPart = "x",
            Tag = "y",
            TtlSeconds = 0,
        }, ApiJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task PostOutbound_StructuredBody_BuildsMessage()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/outbound", new OutboundRequest
        {
            EnvelopeFrom = "noreply@anjal.test",
            EnvelopeTo = "user@example.com",
            Subject = "Test",
            BodyText = "Hello.",
        }, ApiJson.Options);

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        OutboundResponse? body = await res.Content.ReadFromJsonAsync<OutboundResponse>(ApiJson.Options);
        Assert.NotNull(body);
        Assert.Equal("Pending", body!.Status);
        Assert.NotEqual(System.Guid.Empty, body.Id);
    }

    [Fact]
    public async System.Threading.Tasks.Task PostOutbound_RawBytes_PreservesBytes()
    {
        byte[] raw = System.Text.Encoding.UTF8.GetBytes("From: a@b\r\nTo: c@d\r\nSubject: X\r\n\r\nHello.\r\n");
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/outbound", new OutboundRequest
        {
            EnvelopeFrom = "a@b",
            EnvelopeTo = "c@d",
            RawBytesBase64 = System.Convert.ToBase64String(raw),
        }, ApiJson.Options);

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        OutboundResponse? body = await res.Content.ReadFromJsonAsync<OutboundResponse>(ApiJson.Options);
        Assert.NotNull(body);

        // Verify the bytes round-tripped exactly.
        OutboundMessage? stored = await this.store.GetOutboundByIdAsync(body!.Id);
        Assert.NotNull(stored);
        Assert.Equal(raw, stored!.RawBytes);
    }

    [Fact]
    public async System.Threading.Tasks.Task PostOutbound_NoBodyOrRaw_Returns400()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/outbound", new OutboundRequest
        {
            EnvelopeFrom = "a@b",
            EnvelopeTo = "c@d",
        }, ApiJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task PostOutbound_BadBase64_Returns400()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/outbound", new OutboundRequest
        {
            EnvelopeFrom = "a@b",
            EnvelopeTo = "c@d",
            RawBytesBase64 = "not-base-64-!!!",
        }, ApiJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task GetOutbound_KnownId_ReturnsStatus()
    {
        HttpResponseMessage post = await this.client.PostAsJsonAsync("api/outbound", new OutboundRequest
        {
            EnvelopeFrom = "a@b",
            EnvelopeTo = "c@d",
            Subject = "X",
            BodyText = "Y",
        }, ApiJson.Options);
        OutboundResponse? created = await post.Content.ReadFromJsonAsync<OutboundResponse>(ApiJson.Options);
        Assert.NotNull(created);

        HttpResponseMessage get = await this.client.GetAsync(new System.Uri($"api/outbound/{created!.Id}", System.UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        OutboundResponse? fetched = await get.Content.ReadFromJsonAsync<OutboundResponse>(ApiJson.Options);
        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched!.Id);
    }

    [Fact]
    public async System.Threading.Tasks.Task GetOutbound_UnknownId_Returns404()
    {
        HttpResponseMessage res = await this.client.GetAsync(new System.Uri($"api/outbound/{System.Guid.NewGuid()}", System.UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task GetOutbound_BadId_Returns400()
    {
        HttpResponseMessage res = await this.client.GetAsync(new System.Uri("api/outbound/not-a-uuid", System.UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task UnknownRoute_Returns404()
    {
        HttpResponseMessage res = await this.client.GetAsync(new System.Uri("api/nope", System.UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task WrongMethod_Returns405()
    {
        HttpResponseMessage res = await this.client.PutAsync(new System.Uri("api/routing-rules", System.UriKind.Relative), new StringContent(string.Empty));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, res.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task MalformedJson_Returns400()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/routing-rules");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", this.token);
        req.Content = new StringContent("{not json", Encoding.UTF8, "application/json");
        HttpResponseMessage res = await this.client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
