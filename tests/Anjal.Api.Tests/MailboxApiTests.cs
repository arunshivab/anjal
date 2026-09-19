using System.Net;
using System.Net.Http.Json;
using Anjal.Api.Dto;
using Anjal.Mailbox;
using Anjal.Store;

namespace Anjal.Api.Tests;

public class MailboxApiTests : System.IDisposable
{
    private static readonly string[] ArunRecipient = new[] { "arun@anjal.co.in" };

    private readonly InMemoryMessageStore store;
    private readonly MaildirStore maildir;
    private readonly string root;
    private readonly ApiServer server;
    private readonly System.Threading.CancellationTokenSource cts;
    private readonly System.Threading.Tasks.Task serverTask;
    private readonly HttpClient client;
    private readonly string token;
    private bool disposed;

    public MailboxApiTests()
    {
        int port = FreePort.Next();
        this.token = "test-token-" + System.Guid.NewGuid().ToString("N");
        this.store = new InMemoryMessageStore();
        this.root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-api-" + System.Guid.NewGuid().ToString("N"));
        this.maildir = new MaildirStore(this.root, "test");
        this.cts = new System.Threading.CancellationTokenSource();
        this.server = new ApiServer(new ApiOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = port,
            BearerToken = this.token,
        }, this.store, null, this.store, this.maildir);
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
            if (System.IO.Directory.Exists(this.root))
            {
                System.IO.Directory.Delete(this.root, recursive: true);
            }
        }
    }

    private async System.Threading.Tasks.Task<TenantResponse> CreateTenantAsync(string slug = "imagiqa")
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/tenants", new TenantRequest
        {
            Slug = slug,
            DisplayName = "imagiQa",
        }, ApiJson.Options).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<TenantResponse>(ApiJson.Options).ConfigureAwait(false))!;
    }

    private async System.Threading.Tasks.Task CreateDomainAsync(string slug, string domain)
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/tenant-domains", new TenantDomainRequest
        {
            TenantSlug = slug,
            Domain = domain,
        }, ApiJson.Options).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    private async System.Threading.Tasks.Task<MailboxResponse> CreateMailboxAsync(string address, string password = "correct horse battery")
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/mailboxes", new MailboxRequest
        {
            TenantSlug = "imagiqa",
            Address = address,
            Password = password,
            DisplayName = "Arun",
        }, ApiJson.Options).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<MailboxResponse>(ApiJson.Options).ConfigureAwait(false))!;
    }

    // -------- Tenants --------

    [Fact]
    public async System.Threading.Tasks.Task Tenants_Post_Get_List_Delete()
    {
        TenantResponse created = await this.CreateTenantAsync("ImagiQa");
        Assert.Equal("imagiqa", created.Slug);
        Assert.True(created.Enabled);

        HttpResponseMessage get = await this.client.GetAsync("api/tenants/imagiqa");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var list = await this.client.GetFromJsonAsync<System.Collections.Generic.List<TenantResponse>>("api/tenants", ApiJson.Options);
        Assert.Single(list!);

        HttpResponseMessage del = await this.client.DeleteAsync("api/tenants/imagiqa");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        HttpResponseMessage del2 = await this.client.DeleteAsync("api/tenants/imagiqa");
        Assert.Equal(HttpStatusCode.NotFound, del2.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Tenants_Post_InvalidSlug_Returns400()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/tenants", new TenantRequest { Slug = "bad slug!" }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public void IsValidSlug_Rules()
    {
        Assert.True(Endpoints.TenantsHandler.IsValidSlug("imagiqa"));
        Assert.True(Endpoints.TenantsHandler.IsValidSlug("a-1"));
        Assert.False(Endpoints.TenantsHandler.IsValidSlug(string.Empty));
        Assert.False(Endpoints.TenantsHandler.IsValidSlug("-a"));
        Assert.False(Endpoints.TenantsHandler.IsValidSlug("a-"));
        Assert.False(Endpoints.TenantsHandler.IsValidSlug("A"));
        Assert.False(Endpoints.TenantsHandler.IsValidSlug("a.b"));
        Assert.False(Endpoints.TenantsHandler.IsValidSlug(new string('a', 64)));
    }

    [Fact]
    public async System.Threading.Tasks.Task Tenants_SpamThreshold_DefaultKeptOnUpdate_AndSettable()
    {
        TenantResponse created = await this.CreateTenantAsync();
        Assert.Equal(TenantRow.DefaultSpamThreshold, created.SpamThreshold);

        HttpResponseMessage set = await this.client.PostAsJsonAsync("api/tenants", new TenantRequest { Slug = "imagiqa", DisplayName = "x", SpamThreshold = 8 }, ApiJson.Options);
        Assert.Equal(8, (await set.Content.ReadFromJsonAsync<TenantResponse>(ApiJson.Options))!.SpamThreshold);

        HttpResponseMessage keep = await this.client.PostAsJsonAsync("api/tenants", new TenantRequest { Slug = "imagiqa", DisplayName = "y" }, ApiJson.Options);
        Assert.Equal(8, (await keep.Content.ReadFromJsonAsync<TenantResponse>(ApiJson.Options))!.SpamThreshold);

        HttpResponseMessage bad = await this.client.PostAsJsonAsync("api/tenants", new TenantRequest { Slug = "imagiqa", SpamThreshold = -1 }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task SenderRules_Post_List_Delete_Validation()
    {
        await this.CreateTenantAsync();

        HttpResponseMessage post = await this.client.PostAsJsonAsync("api/tenants/imagiqa/sender-rules", new SenderRuleRequest { Pattern = "@Spammer.Test", Action = "block" }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        SenderRuleResponse rule = (await post.Content.ReadFromJsonAsync<SenderRuleResponse>(ApiJson.Options))!;
        Assert.Equal("@spammer.test", rule.Pattern);
        Assert.Equal("block", rule.Action);

        await this.client.PostAsJsonAsync("api/tenants/imagiqa/sender-rules", new SenderRuleRequest { Pattern = "alice@example.com", Action = "allow" }, ApiJson.Options);
        var list = await this.client.GetFromJsonAsync<System.Collections.Generic.List<SenderRuleResponse>>("api/tenants/imagiqa/sender-rules", ApiJson.Options);
        Assert.Equal(2, list!.Count);

        HttpResponseMessage badPattern = await this.client.PostAsJsonAsync("api/tenants/imagiqa/sender-rules", new SenderRuleRequest { Pattern = "no-at", Action = "block" }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, badPattern.StatusCode);
        HttpResponseMessage badAction = await this.client.PostAsJsonAsync("api/tenants/imagiqa/sender-rules", new SenderRuleRequest { Pattern = "@x.test", Action = "maybe" }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, badAction.StatusCode);
        HttpResponseMessage noTenant = await this.client.GetAsync("api/tenants/ghost/sender-rules");
        Assert.Equal(HttpStatusCode.NotFound, noTenant.StatusCode);

        HttpResponseMessage del = await this.client.DeleteAsync("api/tenants/imagiqa/sender-rules/" + System.Uri.EscapeDataString("@spammer.test"));
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        HttpResponseMessage del2 = await this.client.DeleteAsync("api/tenants/imagiqa/sender-rules/" + System.Uri.EscapeDataString("@spammer.test"));
        Assert.Equal(HttpStatusCode.NotFound, del2.StatusCode);
    }

    [Fact]
    public void IsValidSenderPattern_Rules()
    {
        Assert.True(Endpoints.TenantsHandler.IsValidSenderPattern("@example.com"));
        Assert.True(Endpoints.TenantsHandler.IsValidSenderPattern("alice@example.com"));
        Assert.False(Endpoints.TenantsHandler.IsValidSenderPattern("@"));
        Assert.False(Endpoints.TenantsHandler.IsValidSenderPattern("example.com"));
        Assert.False(Endpoints.TenantsHandler.IsValidSenderPattern("a@b@c.d"));
        Assert.False(Endpoints.TenantsHandler.IsValidSenderPattern("a@nodot"));
        Assert.False(Endpoints.TenantsHandler.IsValidSenderPattern("a b@c.d"));
    }

    // -------- Tenant domains --------

    [Fact]
    public async System.Threading.Tasks.Task TenantDomains_Post_UnknownTenant_Returns404()
    {
        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/tenant-domains", new TenantDomainRequest
        {
            TenantSlug = "ghost",
            Domain = "x.test",
        }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task TenantDomains_Post_List_Filter_Delete()
    {
        await this.CreateTenantAsync();
        await this.CreateTenantAsync("other");
        await this.CreateDomainAsync("imagiqa", "Anjal.CO.IN");
        await this.CreateDomainAsync("other", "other.test");

        var all = await this.client.GetFromJsonAsync<System.Collections.Generic.List<TenantDomainResponse>>("api/tenant-domains", ApiJson.Options);
        Assert.Equal(2, all!.Count);
        Assert.Equal("anjal.co.in", all[0].Domain);
        Assert.Equal("imagiqa", all[0].TenantSlug);
        Assert.True(all[0].Verified);

        var mine = await this.client.GetFromJsonAsync<System.Collections.Generic.List<TenantDomainResponse>>("api/tenant-domains?tenant=imagiqa", ApiJson.Options);
        Assert.Single(mine!);

        HttpResponseMessage bad = await this.client.GetAsync("api/tenant-domains?tenant=ghost");
        Assert.Equal(HttpStatusCode.NotFound, bad.StatusCode);

        HttpResponseMessage del = await this.client.DeleteAsync("api/tenant-domains/anjal.co.in");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        HttpResponseMessage del2 = await this.client.DeleteAsync("api/tenant-domains/anjal.co.in");
        Assert.Equal(HttpStatusCode.NotFound, del2.StatusCode);
    }

    // -------- Mailboxes --------

    [Fact]
    public async System.Threading.Tasks.Task Mailboxes_Post_CreatesMaildirAndDefaultFolders_NeverReturnsHash()
    {
        await this.CreateTenantAsync();
        await this.CreateDomainAsync("imagiqa", "anjal.co.in");
        MailboxResponse mb = await this.CreateMailboxAsync("Arun@Anjal.co.in");

        Assert.Equal("arun@anjal.co.in", mb.Address);
        Assert.True(mb.CanAuthenticate);
        Assert.Equal(MailboxRow.DefaultQuotaBytes, mb.QuotaBytes);

        string raw = await (await this.client.GetAsync("api/mailboxes/arun@anjal.co.in")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("pbkdf2", raw, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", raw, System.StringComparison.OrdinalIgnoreCase);

        var folders = await this.client.GetFromJsonAsync<System.Collections.Generic.List<FolderResponse>>("api/mailboxes/arun@anjal.co.in/folders", ApiJson.Options);
        Assert.Equal(5, folders!.Count);
        Assert.Equal("INBOX", folders[0].Name);
        Assert.True(System.IO.Directory.Exists(System.IO.Path.Combine(this.maildir.FolderPath("imagiqa", "arun@anjal.co.in", "Sent"), "cur")));
    }

    [Fact]
    public async System.Threading.Tasks.Task Mailboxes_Post_DomainNotOwned_Returns409()
    {
        await this.CreateTenantAsync();
        await this.CreateTenantAsync("other");
        await this.CreateDomainAsync("other", "other.test");

        HttpResponseMessage res = await this.client.PostAsJsonAsync("api/mailboxes", new MailboxRequest
        {
            TenantSlug = "imagiqa",
            Address = "arun@other.test",
            Password = "correct horse battery",
        }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);

        HttpResponseMessage res2 = await this.client.PostAsJsonAsync("api/mailboxes", new MailboxRequest
        {
            TenantSlug = "imagiqa",
            Address = "arun@unregistered.test",
        }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, res2.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Mailboxes_Post_Validation()
    {
        await this.CreateTenantAsync();
        await this.CreateDomainAsync("imagiqa", "anjal.co.in");

        HttpResponseMessage noAt = await this.client.PostAsJsonAsync("api/mailboxes", new MailboxRequest { TenantSlug = "imagiqa", Address = "arun" }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, noAt.StatusCode);

        HttpResponseMessage plus = await this.client.PostAsJsonAsync("api/mailboxes", new MailboxRequest { TenantSlug = "imagiqa", Address = "arun+x@anjal.co.in" }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, plus.StatusCode);

        HttpResponseMessage shortPw = await this.client.PostAsJsonAsync("api/mailboxes", new MailboxRequest { TenantSlug = "imagiqa", Address = "arun@anjal.co.in", Password = "short" }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, shortPw.StatusCode);

        HttpResponseMessage negQuota = await this.client.PostAsJsonAsync("api/mailboxes", new MailboxRequest { TenantSlug = "imagiqa", Address = "arun@anjal.co.in", QuotaBytes = -1 }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, negQuota.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Mailboxes_Post_ReceiveOnly_ThenUpdateKeepsPasswordWhenOmitted()
    {
        await this.CreateTenantAsync();
        await this.CreateDomainAsync("imagiqa", "anjal.co.in");
        MailboxResponse receiveOnly = await this.CreateMailboxAsync("noreply@anjal.co.in", password: string.Empty);
        Assert.False(receiveOnly.CanAuthenticate);

        MailboxResponse withPw = await this.CreateMailboxAsync("noreply@anjal.co.in");
        Assert.Equal(receiveOnly.Id, withPw.Id);
        Assert.True(withPw.CanAuthenticate);

        MailboxResponse updated = await this.CreateMailboxAsync("noreply@anjal.co.in", password: string.Empty);
        Assert.True(updated.CanAuthenticate);
    }

    [Fact]
    public async System.Threading.Tasks.Task Mailboxes_List_Get_Delete()
    {
        await this.CreateTenantAsync();
        await this.CreateDomainAsync("imagiqa", "anjal.co.in");
        await this.CreateMailboxAsync("arun@anjal.co.in");

        var list = await this.client.GetFromJsonAsync<System.Collections.Generic.List<MailboxResponse>>("api/mailboxes?tenant=imagiqa", ApiJson.Options);
        Assert.Single(list!);

        HttpResponseMessage missing = await this.client.GetAsync("api/mailboxes/nobody@anjal.co.in");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        HttpResponseMessage del = await this.client.DeleteAsync("api/mailboxes/arun@anjal.co.in");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        HttpResponseMessage del2 = await this.client.DeleteAsync("api/mailboxes/arun@anjal.co.in");
        Assert.Equal(HttpStatusCode.NotFound, del2.StatusCode);
    }

    // -------- Messages --------

    [Fact]
    public async System.Threading.Tasks.Task Messages_ListPageAndRaw_RoundTrip()
    {
        await this.CreateTenantAsync();
        await this.CreateDomainAsync("imagiqa", "anjal.co.in");
        await this.CreateMailboxAsync("arun@anjal.co.in");

        // Deliver two messages through the sink so files exist on disk.
        var sink = new MailboxSink(this.store, this.maildir);
        byte[] one = System.Text.Encoding.ASCII.GetBytes("Subject: one\r\nMessage-ID: <1@x>\r\n\r\nfirst\r\n");
        byte[] two = System.Text.Encoding.ASCII.GetBytes("Subject: two\r\nMessage-ID: <2@x>\r\n\r\nsecond\r\n");
        await sink.DeliverAsync(new Smtp.DeliveryContext { EnvelopeFrom = "s@x", EnvelopeTo = ArunRecipient, RawBytes = one });
        await sink.DeliverAsync(new Smtp.DeliveryContext { EnvelopeFrom = "s@x", EnvelopeTo = ArunRecipient, RawBytes = two });

        var page = await this.client.GetFromJsonAsync<MessagePageResponse>("api/mailboxes/arun@anjal.co.in/messages?folder=INBOX&limit=1&offset=0", ApiJson.Options);
        Assert.Equal(2, page!.Total);
        Assert.Single(page.Items);
        Assert.Equal("two", page.Items[0].Subject);

        var second = await this.client.GetFromJsonAsync<MessagePageResponse>("api/mailboxes/arun@anjal.co.in/messages?limit=1&offset=1", ApiJson.Options);
        Assert.Equal("one", second!.Items[0].Subject);

        System.Guid id = second.Items[0].Id;
        var meta = await this.client.GetFromJsonAsync<MessageResponse>($"api/messages/{id}", ApiJson.Options);
        Assert.Equal("1@x", meta!.MessageId);

        var raw = await this.client.GetFromJsonAsync<MessageRawResponse>($"api/messages/{id}/raw", ApiJson.Options);
        Assert.Equal(one, System.Convert.FromBase64String(raw!.RawBytesBase64));

        var folders = await this.client.GetFromJsonAsync<System.Collections.Generic.List<FolderResponse>>("api/mailboxes/arun@anjal.co.in/folders", ApiJson.Options);
        Assert.Equal(2, folders![0].MessageCount);
    }

    [Fact]
    public async System.Threading.Tasks.Task Messages_BadFolderLimitAndId()
    {
        await this.CreateTenantAsync();
        await this.CreateDomainAsync("imagiqa", "anjal.co.in");
        await this.CreateMailboxAsync("arun@anjal.co.in");

        HttpResponseMessage badFolder = await this.client.GetAsync("api/mailboxes/arun@anjal.co.in/messages?folder=Nope");
        Assert.Equal(HttpStatusCode.NotFound, badFolder.StatusCode);

        HttpResponseMessage badLimit = await this.client.GetAsync("api/mailboxes/arun@anjal.co.in/messages?limit=0");
        Assert.Equal(HttpStatusCode.BadRequest, badLimit.StatusCode);

        HttpResponseMessage badId = await this.client.GetAsync("api/messages/not-a-guid");
        Assert.Equal(HttpStatusCode.BadRequest, badId.StatusCode);

        HttpResponseMessage unknownId = await this.client.GetAsync($"api/messages/{System.Guid.NewGuid()}/raw");
        Assert.Equal(HttpStatusCode.NotFound, unknownId.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Routes_RequireAuth()
    {
        using var anon = new HttpClient { BaseAddress = this.client.BaseAddress };
        HttpResponseMessage res = await anon.GetAsync("api/tenants");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
