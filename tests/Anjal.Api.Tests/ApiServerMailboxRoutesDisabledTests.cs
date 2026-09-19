using System.Net;
using Anjal.Store;

namespace Anjal.Api.Tests;

public class ApiServerMailboxRoutesDisabledTests
{
    [Fact]
    public async System.Threading.Tasks.Task WithoutMaildir_MailboxRoutesAre404()
    {
        const int port = 39899;
        var store = new InMemoryMessageStore();
        using var cts = new System.Threading.CancellationTokenSource();
        using var server = new ApiServer(new ApiOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = port,
            BearerToken = string.Empty,
        }, store);
        System.Threading.Tasks.Task task = server.StartAsync(cts.Token);
        await System.Threading.Tasks.Task.Delay(100);

        using var client = new HttpClient { BaseAddress = new System.Uri($"http://127.0.0.1:{port}/") };
        HttpResponseMessage res = await client.GetAsync("api/tenants");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);

        cts.Cancel();
        try { await task; }
        catch (System.OperationCanceledException) { }
    }
}
