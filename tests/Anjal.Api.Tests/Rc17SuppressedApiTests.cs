using System.Net;
using System.Net.Http.Json;
using Anjal.Api.Dto;
using Anjal.Store;

namespace Anjal.Api.Tests;

/// <summary>rc.17 (design review [7d]): an application is told "suppressed" instead of sending again.</summary>
public sealed class Rc17SuppressedApiTests : System.IDisposable
{
    private readonly InMemoryMessageStore store = new();
    private readonly ApiServer server;
    private readonly System.Threading.CancellationTokenSource cts = new();
    private readonly System.Threading.Tasks.Task serverTask;
    private readonly HttpClient client;

    public Rc17SuppressedApiTests()
    {
        int port = FreePort.Next();
        this.server = new ApiServer(new ApiOptions { BindAddress = IPAddress.Loopback, Port = port, BearerToken = "t" }, this.store);
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
    }

    [Fact]
    public async Task AListedAddress_IsAnsweredSuppressed_AndNothingIsQueued()
    {
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "sgh" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "sgh.in" });
        await this.store.SetTenantDocumentAsync(tenant.Id, DoNotSend.AppsKind, "[{\"Id\":\"his\",\"Name\":\"HIS\",\"SendsAs\":\"appointments@sgh.in\"}]");
        await DoNotSend.WriteAsync(this.store, tenant.Id, new DoNotSendDocument { Entries = { new DoNotSendEntry { Address = "v.sharma@yahoo.test", Reason = "No such domain", Added = System.DateTimeOffset.UtcNow, By = DoNotSend.ByAnjal } } });

        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/outbound", new OutboundRequest
        {
            EnvelopeFrom = "appointments@sgh.in",
            EnvelopeTo = "v.sharma@yahoo.test",
            Subject = "Your appointment",
            BodyText = "Confirmed.",
        }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        string body = await res.Content.ReadAsStringAsync();
        Assert.Contains("suppressed", body, System.StringComparison.Ordinal);
        Assert.Contains("No such domain", body, System.StringComparison.Ordinal);
        Assert.Empty(this.store.Outbound);

        HttpResponseMessage other = await this.client.PostAsJsonAsync("api/outbound", new OutboundRequest
        {
            EnvelopeFrom = "appointments@sgh.in",
            EnvelopeTo = "someone@gmail.test",
            Subject = "Your appointment",
            BodyText = "Confirmed.",
        }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.Accepted, other.StatusCode);
    }
}
