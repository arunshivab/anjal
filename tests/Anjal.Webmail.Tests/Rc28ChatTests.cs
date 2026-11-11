using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Anjal.Webmail.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Anjal.Webmail.Tests;

/// <summary>
/// rc.28 (DES-21): chat between an organisation's people - sealed like mail, read by its members only
/// (a late joiner from joining), unread and Seen, removal within 15 minutes, the organisation's switch -
/// and online status from use and from the person's choice.
/// </summary>
public sealed class Rc28ChatTests : IAsyncLifetime, IDisposable
{
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc28c-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly List<HttpClient> clients = new();
    private WebApplication app = null!;
    private string baseAddress = string.Empty;
    private TenantRow tenant = new();
    private MailboxRow arun = new();
    private MailboxRow meena = new();
    private MailboxRow ravi = new();

    public void Dispose()
    {
        foreach (HttpClient c in this.clients)
        {
            c.Dispose();
        }
    }

    public async Task InitializeAsync()
    {
        var maildir = new MaildirStore(Path.Combine(this.root, "mail"), "test");
        this.tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "Imagiqa", PostmasterMailbox = "arun@anjal.co.in" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.tenant.Id, Domain = "anjal.co.in" });
        this.arun = await this.Person("arun", "Arun Shiva B");
        this.meena = await this.Person("meena", "Meena");
        this.ravi = await this.Person("ravi", "Ravi");
        this.app = Program.CreateApp(Array.Empty<string>(), this.store, maildir, "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
        this.baseAddress = this.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        await this.app.StopAsync();
        await this.app.DisposeAsync();
        if (Directory.Exists(this.root))
        {
            TestFolders.Delete(this.root);
        }
    }

    [Fact]
    public async Task AChat_IsSealed_ReadByItsMembers_WithUnreadAndSeen_AndFoundAgain()
    {
        HttpClient a = await this.SignedIn("arun");
        HttpClient m = await this.SignedIn("meena");
        HttpClient r = await this.SignedIn("ravi");
        string page = await a.GetStringAsync("chat?new=1");
        Assert.Contains("Meena", page, StringComparison.Ordinal);
        HttpResponseMessage started = await PostAsync(a, "chat?new=1", "chat/new", ("person", this.meena.Id.ToString()));
        string roomPath = started.Headers.Location!.OriginalString;
        Assert.StartsWith("/chat/", roomPath, StringComparison.Ordinal);
        Guid roomId = Guid.Parse(roomPath[6..]);

        HttpResponseMessage sent = await PostAsync(a, roomPath.TrimStart('/'), roomPath.TrimStart('/') + "/send", ("text", "Can you see the patient in bed 4?"));
        Assert.Equal(HttpStatusCode.Redirect, sent.StatusCode);
        ChatMessageRow stored = Assert.Single(await this.store.ListChatMessagesAsync(roomId, DateTimeOffset.MinValue, 10));
        Assert.StartsWith("s1.", stored.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("bed 4", stored.Body, StringComparison.Ordinal);

        Assert.Equal(1, await this.store.CountUnreadChatsAsync(this.meena.Id));
        string meenaView = WebUtility.HtmlDecode(await m.GetStringAsync(roomPath.TrimStart('/')));
        Assert.Contains("Can you see the patient in bed 4?", meenaView, StringComparison.Ordinal);
        Assert.Equal(0, await this.store.CountUnreadChatsAsync(this.meena.Id));

        // Arun's page asks for news: Meena has seen it.
        using JsonDocument since = JsonDocument.Parse(await a.GetStringAsync(roomPath.TrimStart('/') + "/since?after=0"));
        Assert.Equal("Meena", since.RootElement.GetProperty("seen")[0].GetString());
        Assert.True(since.RootElement.GetProperty("lines")[0].GetProperty("mine").GetBoolean());

        // Ravi is not in it: nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await r.GetAsync(roomPath.TrimStart('/') + "/since?after=0")).StatusCode);
        Assert.DoesNotContain("bed 4", await r.GetStringAsync(roomPath.TrimStart('/')), StringComparison.Ordinal);

        // Starting a chat with Meena again opens the same chat.
        HttpResponseMessage again = await PostAsync(a, "chat?new=1", "chat/new", ("person", this.meena.Id.ToString()));
        Assert.Equal(roomPath, again.Headers.Location!.OriginalString);

        // Removing within 15 minutes: "Message removed" for everyone, the words gone from the store.
        await PostAsync(a, roomPath.TrimStart('/'), $"chat/message/{stored.Id:N}/remove", ("back", roomPath));
        Assert.Contains("Message removed", await m.GetStringAsync(roomPath.TrimStart('/')), StringComparison.Ordinal);
        Assert.Equal(string.Empty, Assert.Single(await this.store.ListChatMessagesAsync(roomId, DateTimeOffset.MinValue, 10)).Body);
        Assert.Single(await this.store.ListAuditAsync(100), e => e.Action == "chat.message-removed");
    }

    [Fact]
    public async Task AGroup_ALateJoinerReadsFromJoining_AndTheOrganisationCanSwitchChatOff()
    {
        HttpClient a = await this.SignedIn("arun");
        HttpClient r = await this.SignedIn("ravi");
        await this.SignedIn("meena");
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        HttpResponseMessage started = await PostAsync(a, "chat?new=1", "chat/new", ("person", this.meena.Id.ToString()), ("name", "Ward 3"));
        string roomPath = started.Headers.Location!.OriginalString;
        await PostAsync(a, roomPath.TrimStart('/'), roomPath.TrimStart('/') + "/send", ("text", "Before Ravi joined."));
        await Task.Delay(20);
        await PostAsync(a, roomPath.TrimStart('/'), roomPath.TrimStart('/') + "/add", ("person", this.ravi.Id.ToString()));
        await Task.Delay(20);
        await PostAsync(a, roomPath.TrimStart('/'), roomPath.TrimStart('/') + "/send", ("text", "After Ravi joined."));
        string raviView = WebUtility.HtmlDecode(await r.GetStringAsync(roomPath.TrimStart('/')));
        Assert.Contains("After Ravi joined.", raviView, StringComparison.Ordinal);
        Assert.DoesNotContain("Before Ravi joined.", raviView, StringComparison.Ordinal);
        Assert.Contains("Ward 3", raviView, StringComparison.Ordinal);

        await svc.SaveMailPolicyAsync(this.tenant.Id, new OrgMailPolicy { Chat = false });
        Assert.False(await svc.ChatOnAsync(this.arun.Id));
        Assert.Contains("when its administrator has it on", await a.GetStringAsync("chat"), StringComparison.Ordinal);
        Assert.DoesNotContain("data-chat-rail", await a.GetStringAsync("folder/INBOX"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_ComesFromUse_AndFromThePersonsChoice()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.Equal("offline", MailboxService.PresenceWord(null, now));
        Assert.Equal("online", MailboxService.PresenceWord(new PresenceRow { LastSeenAt = now.AddMinutes(-2) }, now));
        Assert.Equal("away", MailboxService.PresenceWord(new PresenceRow { LastSeenAt = now.AddMinutes(-10) }, now));
        Assert.Equal("offline", MailboxService.PresenceWord(new PresenceRow { LastSeenAt = now.AddHours(-1) }, now));
        Assert.Equal("busy", MailboxService.PresenceWord(new PresenceRow { LastSeenAt = now, Status = PresenceRow.Busy }, now));
        Assert.Equal("away", MailboxService.PresenceWord(new PresenceRow { LastSeenAt = now, Status = PresenceRow.Away }, now));

        HttpClient m = await this.SignedIn("meena");
        await m.GetStringAsync("chat");
        HttpClient a = await this.SignedIn("arun");
        Assert.Contains("p-online", await a.GetStringAsync("chat?new=1"), StringComparison.Ordinal);
        await PostAsync(m, "chat", "presence", ("status", "busy"), ("note", "In theatre until 3"), ("back", "/chat"));
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        ChatPerson meenaNow = Assert.Single(await svc.ChatColleaguesAsync(this.arun.Id), p => p.Id == this.meena.Id);
        Assert.Equal("busy", meenaNow.Status);
        Assert.Equal("In theatre until 3", meenaNow.Note);
    }

    private async Task<MailboxRow> Person(string local, string name) => await this.store.UpsertMailboxAsync(new MailboxRow
    {
        TenantId = this.tenant.Id,
        LocalPart = local,
        Domain = "anjal.co.in",
        DisplayName = name,
        PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
    });

    private async Task<HttpClient> SignedIn(string local)
    {
        var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(this.baseAddress + "/") };
        this.clients.Add(c);
        string token = Token(await c.GetStringAsync("sign-in"));
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["address"] = local + "@anjal.co.in",
            ["password"] = "correct horse battery",
        });
        Assert.Equal(HttpStatusCode.Redirect, (await c.PostAsync("auth/login", form)).StatusCode);
        return c;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient c, string page, string action, params (string Key, string Value)[] fields)
    {
        var values = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", Token(await c.GetStringAsync(page))) };
        values.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        using var form = new FormUrlEncodedContent(values);
        return await c.PostAsync(action, form);
    }

    private static string Token(string html)
    {
        Match m = TokenRegex.Match(html);
        Assert.True(m.Success, "no antiforgery token");
        return m.Groups[1].Value;
    }
}
