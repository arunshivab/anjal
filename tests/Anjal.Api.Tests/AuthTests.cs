using System.Net;
using Anjal.Store;

namespace Anjal.Api.Tests;

public class AuthTests
{
    private static int nextPort = 38800;

    private static async System.Threading.Tasks.Task StopAsync(System.Threading.CancellationTokenSource cts, System.Threading.Tasks.Task serverTask)
    {
        cts.Cancel();
        try
        {
            await serverTask;
        }
        catch (System.OperationCanceledException)
        {
            // Expected.
        }
#pragma warning disable CA1031 // Test teardown - swallow anything else.
        catch (System.Exception)
        {
            // Server shutdown errors are not the focus of these tests.
        }
#pragma warning restore CA1031
    }

    [Fact]
    public async System.Threading.Tasks.Task MissingAuthHeader_Returns401()
    {
        int p = System.Threading.Interlocked.Increment(ref nextPort);
        var store = new InMemoryMessageStore();
        using var cts = new System.Threading.CancellationTokenSource();
        using var server = new ApiServer(new ApiOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = p,
            BearerToken = "the-token",
        }, store);
        System.Threading.Tasks.Task t = server.StartAsync(cts.Token);
        await System.Threading.Tasks.Task.Delay(100);

        try
        {
            using var client = new HttpClient { BaseAddress = new System.Uri($"http://127.0.0.1:{p}/") };
            HttpResponseMessage res = await client.GetAsync(new System.Uri("api/routing-rules", System.UriKind.Relative));
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
        finally
        {
            await StopAsync(cts, t);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task WrongToken_Returns401()
    {
        int p = System.Threading.Interlocked.Increment(ref nextPort);
        var store = new InMemoryMessageStore();
        using var cts = new System.Threading.CancellationTokenSource();
        using var server = new ApiServer(new ApiOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = p,
            BearerToken = "the-token",
        }, store);
        System.Threading.Tasks.Task t = server.StartAsync(cts.Token);
        await System.Threading.Tasks.Task.Delay(100);

        try
        {
            using var client = new HttpClient { BaseAddress = new System.Uri($"http://127.0.0.1:{p}/") };
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "wrong-token");
            HttpResponseMessage res = await client.GetAsync(new System.Uri("api/routing-rules", System.UriKind.Relative));
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
        finally
        {
            await StopAsync(cts, t);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task CorrectToken_Returns200()
    {
        int p = System.Threading.Interlocked.Increment(ref nextPort);
        var store = new InMemoryMessageStore();
        using var cts = new System.Threading.CancellationTokenSource();
        using var server = new ApiServer(new ApiOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = p,
            BearerToken = "the-token",
        }, store);
        System.Threading.Tasks.Task t = server.StartAsync(cts.Token);
        await System.Threading.Tasks.Task.Delay(100);

        try
        {
            using var client = new HttpClient { BaseAddress = new System.Uri($"http://127.0.0.1:{p}/") };
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "the-token");
            HttpResponseMessage res = await client.GetAsync(new System.Uri("api/routing-rules", System.UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }
        finally
        {
            await StopAsync(cts, t);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task EmptyConfiguredToken_DisablesAuth()
    {
        // Empty token means "no auth required" - a deliberate test-only mode.
        int p = System.Threading.Interlocked.Increment(ref nextPort);
        var store = new InMemoryMessageStore();
        using var cts = new System.Threading.CancellationTokenSource();
        using var server = new ApiServer(new ApiOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = p,
            BearerToken = string.Empty,
        }, store);
        System.Threading.Tasks.Task t = server.StartAsync(cts.Token);
        await System.Threading.Tasks.Task.Delay(100);

        try
        {
            using var client = new HttpClient { BaseAddress = new System.Uri($"http://127.0.0.1:{p}/") };
            HttpResponseMessage res = await client.GetAsync(new System.Uri("api/routing-rules", System.UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }
        finally
        {
            await StopAsync(cts, t);
        }
    }
}
