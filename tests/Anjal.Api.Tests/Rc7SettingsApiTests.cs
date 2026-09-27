using System.Net;
using System.Net.Http.Json;
using Anjal.Api.Dto;
using Anjal.Api.Endpoints;
using Anjal.Store;

namespace Anjal.Api.Tests;

/// <summary>v1.0.0-rc.7: <c>/api/settings</c> and the tenant's unencrypted-mail folder.</summary>
public sealed class Rc7SettingsApiTests : System.IDisposable
{
    private readonly InMemoryMessageStore store = new();
    private readonly ApiServer server;
    private readonly System.Threading.CancellationTokenSource cts = new();
    private readonly Task serverTask;
    private readonly HttpClient client;

    public Rc7SettingsApiTests()
    {
        int port = FreePort.Next();
        string token = "test-token-" + System.Guid.NewGuid().ToString("N");
        this.server = new ApiServer(new ApiOptions { BindAddress = IPAddress.Loopback, Port = port, BearerToken = token }, this.store);
        this.serverTask = this.server.StartAsync(this.cts.Token);
        System.Threading.Thread.Sleep(100);
        this.client = new HttpClient { BaseAddress = new System.Uri($"http://127.0.0.1:{port}/") };
        this.client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    public void Dispose()
    {
        this.client.Dispose();
        this.cts.Cancel();
        try
        {
            this.serverTask.Wait(2000);
        }
        catch (System.AggregateException)
        {
            // Stopping.
        }
        this.server.Dispose();
        this.cts.Dispose();
    }

    [Fact]
    public async Task Put_List_Delete_OneSetting()
    {
        HttpResponseMessage put = await this.client.PutAsJsonAsync("api/settings/server/ANJAL_TLS_DEFAULT_MODE", new SettingRequest { Value = "opportunistic" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        SettingsListResponse? list = await this.client.GetFromJsonAsync<SettingsListResponse>("api/settings");
        SettingResponse only = Assert.Single(list!.Server);
        Assert.Equal("ANJAL_TLS_DEFAULT_MODE", only.Key);
        Assert.Equal("api", only.UpdatedBy);
        Assert.Empty(list.Webmail);
        Assert.Equal(HttpStatusCode.NoContent, (await this.client.DeleteAsync("api/settings/server/ANJAL_TLS_DEFAULT_MODE")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await this.client.DeleteAsync("api/settings/server/ANJAL_TLS_DEFAULT_MODE")).StatusCode);
    }

    [Fact]
    public async Task Secrets_AndUnknownScopes_AreRefused()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await this.client.PutAsJsonAsync("api/settings/server/ANJAL_KEK", new SettingRequest { Value = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await this.client.PutAsJsonAsync("api/settings/server/ANJAL_POSTGRES", new SettingRequest { Value = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await this.client.PutAsJsonAsync("api/settings/other/ANJAL_BIND", new SettingRequest { Value = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await this.client.PutAsJsonAsync("api/settings/server/PATH", new SettingRequest { Value = "x" })).StatusCode);
        Assert.Empty(await this.store.ListSettingsAsync("server"));
    }

    [Theory]
    [InlineData("Unencrypted", true)]
    [InlineData("Not encrypted", true)]
    [InlineData("INBOX", false)]
    [InlineData("junk", false)]
    [InlineData("a/b", false)]
    [InlineData(".hidden", false)]
    public void UnencryptedFolder_Names(string name, bool ok)
    {
        Assert.Equal(ok, TenantsHandler.IsFolderForUnencrypted(name));
    }
}
