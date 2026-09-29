using System.Net;
using System.Net.Http.Json;
using Anjal.Api.Dto;
using Anjal.Store;

namespace Anjal.Api.Tests;

/// <summary>v1.0.0-rc.9 (DEF-068, D-53): the API retries for 5 days by default; a caller's own give-up time is kept.</summary>
public sealed class Rc9GiveUpApiTests : System.IDisposable
{
    private readonly InMemoryMessageStore store = new();
    private readonly ApiServer server;
    private readonly System.Threading.CancellationTokenSource cts = new();
    private readonly System.Threading.Tasks.Task serverTask;
    private readonly HttpClient client;

    public Rc9GiveUpApiTests()
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

    private async Task<OutboundMessage> QueueAsync(int giveUpHours)
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/outbound", new OutboundRequest
        {
            EnvelopeFrom = "his@anjal.co.in",
            EnvelopeTo = "patient@example.com",
            Subject = "Report ready",
            BodyText = "Your report is ready.",
            GiveUpHours = giveUpHours,
        }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        OutboundResponse body = (await res.Content.ReadFromJsonAsync<OutboundResponse>(ApiJson.Options))!;
        return (await this.store.GetOutboundByIdAsync(body.Id))!;
    }

    [Fact]
    public async Task WithoutAGiveUpTime_TheApiRetriesForFiveDays()
    {
        OutboundMessage m = await this.QueueAsync(0);
        Assert.InRange(m.GiveUpAt - m.CreatedAt, System.TimeSpan.FromDays(5) - System.TimeSpan.FromMinutes(1), System.TimeSpan.FromDays(5) + System.TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task ACallersOwnGiveUpTime_IsKept()
    {
        OutboundMessage m = await this.QueueAsync(6);
        Assert.InRange(m.GiveUpAt - m.CreatedAt, System.TimeSpan.FromHours(6) - System.TimeSpan.FromMinutes(1), System.TimeSpan.FromHours(6) + System.TimeSpan.FromMinutes(1));
    }
}
