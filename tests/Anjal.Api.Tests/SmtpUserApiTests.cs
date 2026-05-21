using System.Net;
using System.Net.Http.Json;
using Anjal.Api.Dto;
using Anjal.Store;

namespace Anjal.Api.Tests;

public class SmtpUserApiTests : System.IDisposable
{
    private static int nextPort = 39600;

    private readonly InMemoryMessageStore store;
    private readonly ApiServer server;
    private readonly System.Threading.CancellationTokenSource cts;
    private readonly System.Threading.Tasks.Task serverTask;
    private readonly HttpClient client;
    private readonly string token;
    private bool disposed;

    public SmtpUserApiTests()
    {
        int port = System.Threading.Interlocked.Increment(ref nextPort);
        this.token = "test-token-" + System.Guid.NewGuid().ToString("N");
        this.store = new InMemoryMessageStore();
        this.cts = new System.Threading.CancellationTokenSource();
        this.server = new ApiServer(new ApiOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = port,
            BearerToken = this.token,
        }, this.store);
        this.serverTask = this.server.StartAsync(this.cts.Token);
        System.Threading.Thread.Sleep(100);

        this.client = new HttpClient { BaseAddress = new System.Uri($"http://127.0.0.1:{port}/") };
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
        if (this.disposed) return;
        this.disposed = true;
        if (disposing)
        {
            this.client.Dispose();
            this.cts.Cancel();
            try { this.serverTask.Wait(2000); }
#pragma warning disable CA1031
            catch (System.Exception) { }
#pragma warning restore CA1031
            this.server.Dispose();
            this.cts.Dispose();
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Post_CreatesUser()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/smtp-users", new SmtpUserRequest
        {
            Username = "alice",
            Password = "Sekret12345",
            AllowedFromDomains = new System.Collections.Generic.List<string> { "hospital-a.test" },
        }, ApiJson.Options);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        SmtpUserResponse? body = await res.Content.ReadFromJsonAsync<SmtpUserResponse>(ApiJson.Options);
        Assert.NotNull(body);
        Assert.Equal("alice", body!.Username);
        Assert.True(body.Enabled);
    }

    [Fact]
    public async System.Threading.Tasks.Task Post_DoesNotReturnPasswordHash()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/smtp-users", new SmtpUserRequest
        {
            Username = "alice",
            Password = "Sekret12345",
        }, ApiJson.Options);

        string raw = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("pbkdf2", raw, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Sekret12345", raw, System.StringComparison.Ordinal);
        Assert.DoesNotContain("password", raw, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async System.Threading.Tasks.Task Post_HashesPassword_StoreContainsPbkdf2Format()
    {
        await this.client.PostAsJsonAsync("api/smtp-users", new SmtpUserRequest
        {
            Username = "alice",
            Password = "Sekret12345",
        }, ApiJson.Options);

        var row = await this.store.GetSmtpUserAsync("alice");
        Assert.NotNull(row);
        Assert.StartsWith("pbkdf2$", row!.PasswordPbkdf2, System.StringComparison.Ordinal);
        // Should NOT contain plaintext.
        Assert.DoesNotContain("Sekret12345", row.PasswordPbkdf2, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task Post_MissingPassword_Returns400()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/smtp-users", new SmtpUserRequest
        {
            Username = "alice",
        }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Post_ShortPassword_Returns400()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/smtp-users", new SmtpUserRequest
        {
            Username = "alice",
            Password = "short",
        }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Post_MissingUsername_Returns400()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/smtp-users", new SmtpUserRequest
        {
            Password = "Sekret12345",
        }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Get_ListsUsers_NoHashes()
    {
        await this.client.PostAsJsonAsync("api/smtp-users", new SmtpUserRequest
        {
            Username = "alice",
            Password = "Sekret12345",
        }, ApiJson.Options);
        await this.client.PostAsJsonAsync("api/smtp-users", new SmtpUserRequest
        {
            Username = "bob",
            Password = "Another12345",
        }, ApiJson.Options);

        HttpResponseMessage res = await this.client.GetAsync(new System.Uri("api/smtp-users", System.UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        string raw = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("pbkdf2", raw, System.StringComparison.Ordinal);

        var list = await res.Content.ReadFromJsonAsync<System.Collections.Generic.List<SmtpUserResponse>>(ApiJson.Options);
        Assert.NotNull(list);
        Assert.Equal(2, list!.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task Delete_RemovesUser()
    {
        await this.client.PostAsJsonAsync("api/smtp-users", new SmtpUserRequest
        {
            Username = "doomed",
            Password = "Sekret12345",
        }, ApiJson.Options);

        HttpResponseMessage del = await this.client.DeleteAsync(new System.Uri("api/smtp-users/doomed", System.UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        HttpResponseMessage del2 = await this.client.DeleteAsync(new System.Uri("api/smtp-users/doomed", System.UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, del2.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Post_NormalizesAllowedDomains()
    {
        // Mixed case, with whitespace and duplicates.
        await this.client.PostAsJsonAsync("api/smtp-users", new SmtpUserRequest
        {
            Username = "alice",
            Password = "Sekret12345",
            AllowedFromDomains = new System.Collections.Generic.List<string>
            {
                "  Hospital-A.TEST  ",
                "hospital-a.test",  // duplicate after normalization
                "Hospital-B.test",
            },
        }, ApiJson.Options);

        var row = await this.store.GetSmtpUserAsync("alice");
        Assert.NotNull(row);
        Assert.Equal(2, row!.AllowedFromDomains.Count);
        Assert.True(System.Linq.Enumerable.Contains(row.AllowedFromDomains, "hospital-a.test"));
        Assert.True(System.Linq.Enumerable.Contains(row.AllowedFromDomains, "hospital-b.test"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Delete_RequiresAuth()
    {
        using var noauth = new HttpClient { BaseAddress = this.client.BaseAddress };
        HttpResponseMessage res = await noauth.DeleteAsync(new System.Uri("api/smtp-users/x", System.UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
