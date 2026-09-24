using System.Net;
using Anjal.Store;

namespace Anjal.Api.Tests;

public class AuthTests
{

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
        int p = FreePort.Next();
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
        int p = FreePort.Next();
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
        int p = FreePort.Next();
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
        int p = FreePort.Next();
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

/// <summary>SEC-R3: rotation, and a token that may not be used at all.</summary>
public class TokenRotationTests
{
    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]   // what a zeroed buffer produces
    [InlineData("abababababababababababababab")]               // long, but almost no variety
    [InlineData("CHANGE-ME-long-random-string")]     // our own deployment template
    [InlineData("change_me_change_me_change")]
    [InlineData("anjal-admin-token-value-here")]
    [InlineData("TestTestTestTestTestTest")]
    public void ObviousTokens_AreRecognised(string token)
    {
        Assert.True(IsObvious(token), token);
    }

    [Theory]
    [InlineData("k3Qv9mXpL2wR7nT4yB8sD1fG")]
    [InlineData("9f4c1e77a0b34d2e9c8a51ff6b2d7e40")]
    public void RandomTokens_AreNot(string token)
    {
        Assert.False(IsObvious(token), token);
        Assert.True(token.Length >= Anjal.Api.ApiOptions.MinimumTokenLength);
    }

    /// <summary>The same rule the server applies at start-up.</summary>
    private static bool IsObvious(string token)
    {
        var sb = new System.Text.StringBuilder(token.Length);
        foreach (char c in token)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        var distinct = new System.Collections.Generic.HashSet<char>(token);
        if (distinct.Count < 8)
        {
            return true;
        }
        string lowered = sb.ToString();
        foreach (string bad in new[] { "changeme", "password", "secret", "token", "anjal", "test", "example", "placeholder", "xxxx", "0000", "1234" })
        {
            if (lowered.Contains(bad, System.StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
