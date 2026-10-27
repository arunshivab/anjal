using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Anjal.Webmail.AppApi;
using Anjal.Webmail.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Anjal.Webmail.Tests;

/// <summary>
/// rc.26 (DES-14 v1.0, Parts 1-3): the application API against the real endpoints - keys, Allowed
/// from, signing, sending and its status, idempotency, limits, the sandbox, batches, templates, the
/// do-not-send list, events, the inbox encrypted to the application's key, shared addresses, the
/// return path's reports, webhooks, and the API description.
/// </summary>
public sealed class Rc26AppApiTests : IAsyncLifetime, IDisposable
{
    private static readonly string[] Outside = { "priya@gmail.test" };
    private static readonly string[] PriyaMeera = new[] { "priya@gmail.test", "meera@sgh.in" };
    private static readonly string[] SignInTag = new[] { "signin" };
    private static readonly string[] Ravi = new[] { "ravi@gmail.test" };
    private static readonly string[] GonePriya = new[] { "gone@gmail.test", "priya@gmail.test" };
    private static readonly string[] Gone = new[] { "gone@gmail.test" };
    private static readonly string[] NotAnAddress = new[] { "not-an-address" };
    private static readonly string[] CodeMinutes = new[] { "code", "minutes" };
    private static readonly string[] Reports = new[] { "reports@sgh.in" };
    private static readonly string[] MeeraOnly = new[] { "meera@sgh.in" };
    private static readonly string[] PriyaRavi = new[] { "priya@gmail.test", "ravi@gmail.test" };
    private static readonly string[] PriyaSandboxBounce = new[] { "priya@gmail.test", MailboxService.SandboxBounce };
    private static readonly string[] CodeTag = new[] { "code" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc26api-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly List<HttpClient> clients = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private MailboxService svc = null!;
    private string baseAddress = string.Empty;
    private TenantRow sgh = new();
    private MailboxRow arun = new();
    private MailboxRow meera = new();
    private string appId = string.Empty;
    private string liveKey = string.Empty;
    private string testKey = string.Empty;

    public void Dispose()
    {
        foreach (HttpClient c in this.clients)
        {
            c.Dispose();
        }
    }

    public async Task InitializeAsync()
    {
        MailboxService.SandboxDelay = TimeSpan.FromMilliseconds(50);
        MailboxService.AllowPlainWebhooks = true;
        AppWebhookWorker.Interval = TimeSpan.FromHours(1);
        this.maildir = new MaildirStore(this.root, "test");
        this.sgh = await this.store.UpsertTenantAsync(new TenantRow { Slug = "sgh", DisplayName = "Sri Ganesh Hospital", PostmasterMailbox = "arun@sgh.in" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.sgh.Id, Domain = "sgh.in" });
        this.arun = await this.Person("arun", "Arun Shiva B");
        this.meera = await this.Person("meera", "Meera Iyer");
        this.app = Program.CreateApp(Array.Empty<string>(), this.store, this.maildir, "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
        this.svc = this.app.Services.GetRequiredService<MailboxService>();
        this.baseAddress = this.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        (string? error, string? id, _, _) = await this.svc.AddAppAsync(this.sgh, "Hospital information system", "Sends notifications", "no-reply@sgh.in", string.Empty);
        Assert.Null(error);
        this.appId = id!;
        this.liveKey = (await this.svc.NewHttpKeyAsync(this.sgh, this.appId, sandbox: false, "the HIS server", null)).Key!;
        this.testKey = (await this.svc.NewHttpKeyAsync(this.sgh, this.appId, sandbox: true, "the HIS test bench", null)).Key!;
    }

    public async Task DisposeAsync()
    {
        await this.app.StopAsync();
        await this.app.DisposeAsync();
        MailboxService.AllowPlainWebhooks = false;
        if (Directory.Exists(this.root))
        {
            TestFolders.Delete(this.root);
        }
    }

    [Fact]
    public async Task Send_IsQueuedWithItsOwnReturnPath_DeliveredHere_AndItsStatusAndEventsFollowTheQueue()
    {
        HttpClient c = this.Client();
        HttpResponseMessage sent = await Send(c, this.liveKey, Body(PriyaMeera, tags: SignInTag), "key-1");
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        Assert.True(sent.Headers.Contains("Anjal-Request-Id"));
        Assert.Equal("60", sent.Headers.GetValues("X-RateLimit-Limit").Single());
        using JsonDocument body = JsonDocument.Parse(await sent.Content.ReadAsStringAsync());
        Guid id = body.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("queued", body.RootElement.GetProperty("status").GetString());

        // Outside: one queue row with the send's id and its return path; here: delivered at once.
        OutboundMessage row = Assert.Single(this.store.Outbound);
        Assert.Equal(id, row.AppSendId);
        Assert.Equal(AppBounceAddress.For(id, "bounce.anjal.localhost"), row.EnvelopeFrom);
        Assert.Equal(id, AppBounceAddress.SendOf(row.EnvelopeFrom));
        Assert.Contains(this.store.MailboxMessages, m => m.MailboxId == this.meera.Id);
        string wire = Encoding.UTF8.GetString(row.RawBytes);
        Assert.Contains("From: \"HIS\" <no-reply@sgh.in>", wire, StringComparison.Ordinal);
        Assert.Contains("X-His-Purpose: signin", wire, StringComparison.Ordinal);

        // Status: no subject (DES-14 §28.2); tags and metadata come back.
        using JsonDocument status = JsonDocument.Parse(await (await Get(c, this.liveKey, "messages/" + id)).Content.ReadAsStringAsync());
        Assert.Equal("queued", status.RootElement.GetProperty("status").GetString());
        Assert.False(status.RootElement.TryGetProperty("subject", out _));
        Assert.Equal("signin", status.RootElement.GetProperty("tags")[0].GetString());
        Assert.Equal("u_8841", status.RootElement.GetProperty("metadata").GetProperty("userId").GetString());

        // The worker's result becomes the send's event and status.
        await this.store.MarkOutboundResultAsync(row.Id, OutboundStatus.Pending, DateTimeOffset.UtcNow.AddMinutes(5), "451 4.7.1 try later");
        Assert.Equal("deferred", await this.StatusOf(c, id));
        await this.store.MarkOutboundResultAsync(row.Id, OutboundStatus.Sent, DateTimeOffset.UtcNow, "250 2.0.0 OK");
        Assert.Equal("delivered", await this.StatusOf(c, id));
        using JsonDocument events = JsonDocument.Parse(await (await Get(c, this.liveKey, "events")).Content.ReadAsStringAsync());
        List<string> types = events.RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("type").GetString()!).ToList();
        Assert.Contains("message.queued", types);
        Assert.Contains("message.deferred", types);
        Assert.Contains("message.delivered", types);
        JsonElement delivered = events.RootElement.GetProperty("events").EnumerateArray().Last(e => e.GetProperty("type").GetString() == "message.delivered");
        Assert.Equal(id, delivered.GetProperty("message").GetProperty("id").GetGuid());
        Assert.Equal("signin", delivered.GetProperty("message").GetProperty("tags")[0].GetString());
        long next = events.RootElement.GetProperty("next").GetInt64();
        using JsonDocument none = JsonDocument.Parse(await (await Get(c, this.liveKey, "events?since=" + next)).Content.ReadAsStringAsync());
        Assert.Equal(0, none.RootElement.GetProperty("events").GetArrayLength());

        // The list, newest first, by tag.
        using JsonDocument list = JsonDocument.Parse(await (await Get(c, this.liveKey, "messages?tag=signin")).Content.ReadAsStringAsync());
        Assert.Equal(id, list.RootElement.GetProperty("messages")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Idempotency_TheSameKeyAndBodyReplays_ADifferentBodyIsAMismatch()
    {
        HttpClient c = this.Client();
        string first = await (await Send(c, this.liveKey, Body(Outside), "order-77")).Content.ReadAsStringAsync();
        HttpResponseMessage again = await Send(c, this.liveKey, Body(Outside), "order-77");
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal("true", again.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(JsonDocument.Parse(first).RootElement.GetProperty("id").GetGuid(), JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid());
        Assert.Single(this.store.Outbound);
        HttpResponseMessage other = await Send(c, this.liveKey, Body(Ravi), "order-77");
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Equal("idempotency_mismatch", await ErrorOf(other));
        HttpResponseMessage tooLong = await Send(c, this.liveKey, Body(Outside), new string('k', 129));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task Keys_UnknownWrongAndMismatched401_BearerWorks_RetiredIs403WithItsFingerprint_AndTenFailuresThrottle()
    {
        HttpClient c = this.Client();
        Assert.Equal("invalid_key", await ErrorOf(await Send(c, null, Body(Outside))));
        Assert.Equal("invalid_key", await ErrorOf(await Send(c, "ak_live_" + new string('x', 32), Body(Outside))));
        using (var both = new HttpRequestMessage(HttpMethod.Get, "api/v1/limits"))
        {
            both.Headers.Add("X-Api-Key", this.liveKey);
            both.Headers.Authorization = new AuthenticationHeaderValue("Bearer", this.testKey);
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(both)).StatusCode);
        }
        using (var bearer = new HttpRequestMessage(HttpMethod.Get, "api/v1/limits"))
        {
            bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", this.liveKey);
            Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(bearer)).StatusCode);
        }
        OrgApp app = (await this.svc.AppOfAsync(this.sgh.Id, this.appId))!;
        AppHttpKey live = app.HttpKeys.First(k => !k.Sandbox);
        Assert.NotNull(live.LastUsed);
        Assert.Null(await this.svc.RetireHttpKeyAsync(this.sgh, this.appId, live.Id));
        HttpResponseMessage retired = await Get(c, this.liveKey, "limits");
        Assert.Equal(HttpStatusCode.Forbidden, retired.StatusCode);
        using JsonDocument r = JsonDocument.Parse(await retired.Content.ReadAsStringAsync());
        Assert.Equal("key_retired", r.RootElement.GetProperty("error").GetString());
        Assert.Equal(live.Fingerprint, r.RootElement.GetProperty("fingerprint").GetString());
        Assert.Equal(MailboxService.HttpKeyFingerprint(this.liveKey), live.Fingerprint);
        Assert.Contains((await this.store.ListAuditAsync(50)), a => a.Action == "app.api.invalid-key");

        // D-286: two live keys of each kind; a third waits for one to be retired.
        Assert.NotNull((await this.svc.NewHttpKeyAsync(this.sgh, this.appId, sandbox: false, "second", null)).Key);
        Assert.NotNull((await this.svc.NewHttpKeyAsync(this.sgh, this.appId, sandbox: false, "third", null)).Key);
        Assert.Equal("Two keys of this kind are live already. Retire one first.", (await this.svc.NewHttpKeyAsync(this.sgh, this.appId, sandbox: false, "fourth", null)).Error);
        Assert.Equal("Two keys of this kind are live already. Retire one first.", (await this.svc.NewHttpKeyAsync(this.sgh, this.appId, sandbox: true, "second test", DateTimeOffset.UtcNow.AddDays(30))) is { Error: null } ? (await this.svc.NewHttpKeyAsync(this.sgh, this.appId, sandbox: true, "third test", null)).Error : "first refused");

        for (int i = 0; i < 8; i++)
        {
            await Get(c, "ak_live_" + new string('y', 32), "limits");
        }
        HttpResponseMessage throttled = await Get(c, this.testKey, "limits");
        Assert.Equal((HttpStatusCode)429, throttled.StatusCode);
        Assert.Equal("too_many_failures", await ErrorOf(throttled));
        Assert.NotNull(throttled.Headers.RetryAfter);
    }

    [Fact]
    public async Task AllowedFrom_RefusesOtherAddresses_BeforeAnythingElse()
    {
        HttpClient c = this.Client();
        Assert.Null(await this.svc.SaveAppApiSettingsAsync(this.sgh, this.appId, string.Empty, 200, 1000, "203.0.113.0/24", false));
        HttpResponseMessage refused = await Get(c, this.liveKey, "limits");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("address_not_allowed", await ErrorOf(refused));
        Assert.Contains((await this.store.ListAuditAsync(20)), a => a.Action == "app.api.address-refused");
        // A loopback range needs the operator's permission for the organisation.
        Assert.NotNull(await this.svc.SaveAppApiSettingsAsync(this.sgh, this.appId, string.Empty, 200, 1000, "127.0.0.0/8", false));
        await this.AllowPrivateAsync();
        Assert.Null(await this.svc.SaveAppApiSettingsAsync(this.sgh, this.appId, string.Empty, 200, 1000, "127.0.0.0/8", false));
        Assert.Equal(HttpStatusCode.OK, (await Get(c, this.liveKey, "limits")).StatusCode);
    }

    [Fact]
    public async Task TheBody_IsChecked_SenderFieldsContentAndType()
    {
        HttpClient c = this.Client();
        HttpResponseMessage stranger = await Send(c, this.liveKey, Body(Outside, from: "someone@sgh.in"));
        Assert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode);
        Assert.Equal("sender_not_allowed", await ErrorOf(stranger));
        Assert.Null(await this.svc.SaveAppApiSettingsAsync(this.sgh, this.appId, "@sgh.in", 200, 1000, string.Empty, false));
        Assert.Equal(HttpStatusCode.Accepted, (await Send(c, this.liveKey, Body(Outside, from: "someone@sgh.in"))).StatusCode);
        Assert.NotNull(await this.svc.SaveAppApiSettingsAsync(this.sgh, this.appId, "@gmail.test", 200, 1000, string.Empty, false));

        HttpResponseMessage noText = await Send(c, this.liveKey, "{\"from\":{\"email\":\"no-reply@sgh.in\"},\"to\":{\"address\":\"priya@gmail.test\"},\"subject\":\"x\"}");
        Assert.Equal((HttpStatusCode)422, noText.StatusCode);
        using JsonDocument why = JsonDocument.Parse(await noText.Content.ReadAsStringAsync());
        Assert.Equal("invalid_request", why.RootElement.GetProperty("error").GetString());
        Assert.Equal("text", why.RootElement.GetProperty("field").GetString());
        Assert.Equal("invalid_request", await ErrorOf(await Send(c, this.liveKey, Body(Enumerable.Range(0, 51).Select(i => $"p{i}@gmail.test").ToArray()))));
        Assert.Equal("invalid_request", await ErrorOf(await Send(c, this.liveKey, Body(Outside).Replace("\"tags\"", "\"colour\"", StringComparison.Ordinal))));
        Assert.Equal("invalid_json", await ErrorOf(await Send(c, this.liveKey, "{not json")));

        byte[] program = new byte[0x80];
        program[0] = 0x4D;
        program[1] = 0x5A;
        program[0x3C] = 0x40;
        "PE\0\0"u8.ToArray().CopyTo(program, 0x40);
        string withProgram = Body(Outside)[..^1] + ",\"attachments\":[{\"filename\":\"report.pdf\",\"contentType\":\"application/pdf\",\"content\":\"" + Convert.ToBase64String(program) + "\"}]}";
        HttpResponseMessage refused = await Send(c, this.liveKey, withProgram);
        Assert.Equal((HttpStatusCode)422, refused.StatusCode);
        Assert.Equal("content_refused", await ErrorOf(refused));

        using var form = new StringContent(Body(Outside), Encoding.UTF8, "text/plain");
        using var plain = new HttpRequestMessage(HttpMethod.Post, "api/v1/messages/email") { Content = form };
        plain.Headers.Add("X-Api-Key", this.liveKey);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await c.SendAsync(plain)).StatusCode);
    }

    [Fact]
    public async Task Limits_TheHourIsKept_429WithRetryAfter_AndNothingIsQueued()
    {
        HttpClient c = this.Client();
        Assert.Null(await this.svc.SaveAppApiSettingsAsync(this.sgh, this.appId, string.Empty, 2, 1000, string.Empty, false));
        Assert.NotNull(await this.svc.SaveAppApiSettingsAsync(this.sgh, this.appId, string.Empty, 2001, 1000, string.Empty, false));
        Assert.Equal(HttpStatusCode.Accepted, (await Send(c, this.liveKey, Body(Outside))).StatusCode);
        HttpResponseMessage second = await Send(c, this.liveKey, Body(Outside));
        Assert.Equal("0", second.Headers.GetValues("X-RateLimit-Remaining").Single());
        HttpResponseMessage third = await Send(c, this.liveKey, Body(Outside));
        Assert.Equal((HttpStatusCode)429, third.StatusCode);
        using JsonDocument why = JsonDocument.Parse(await third.Content.ReadAsStringAsync());
        Assert.Equal("rate_limited", why.RootElement.GetProperty("error").GetString());
        Assert.Equal("hour", why.RootElement.GetProperty("window").GetString());
        Assert.True(why.RootElement.GetProperty("retryAfter").GetInt32() > 0);
        Assert.NotNull(third.Headers.RetryAfter);
        Assert.Equal(2, this.store.Outbound.Count);
        using JsonDocument limits = JsonDocument.Parse(await (await Get(c, this.liveKey, "limits")).Content.ReadAsStringAsync());
        JsonElement hour = limits.RootElement.GetProperty("windows").EnumerateArray().Single(w => w.GetProperty("window").GetString() == "hour");
        Assert.Equal(2, hour.GetProperty("used").GetInt64());
        Assert.Equal(0, hour.GetProperty("remaining").GetInt64());
    }

    [Fact]
    public async Task Item80_TheDraftKeptWhenTheSessionEnds_IsRefusedAfterSessionsWereEndedOnPurpose_OrForAStoppedMailbox()
    {
        MailboxRow writer = await this.Person("kavya", "Kavya");
        Microsoft.AspNetCore.DataProtection.ITimeLimitedDataProtector protector = Microsoft.AspNetCore.DataProtection.DataProtectionAdvancedExtensions.ToTimeLimitedDataProtector(
            this.app.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>().CreateProtector("anjal.draft-keep.v1"));
        string Token(DateTimeOffset issued) => Microsoft.AspNetCore.DataProtection.DataProtectionAdvancedExtensions.Protect(protector, writer.Id.ToString("N") + "|" + issued.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), TimeSpan.FromDays(1));
        HttpClient c = this.Client();
        async Task<HttpStatusCode> KeepAsync(string token)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "draft/keep")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["keep"] = token, ["to"] = "ravi@sgh.in", ["subject"] = "Ward round", ["body"] = "Notes so far" }),
            };
            req.Headers.Add("X-Requested-With", "anjal");
            return (await c.SendAsync(req)).StatusCode;
        }

        string before = Token(DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal(HttpStatusCode.OK, await KeepAsync(before));
        await this.app.Services.GetRequiredService<SessionRegistry>().EndOthersAsync(writer.Id, null);
        Assert.Equal(HttpStatusCode.Forbidden, await KeepAsync(before));
        Assert.Equal(HttpStatusCode.OK, await KeepAsync(Token(DateTimeOffset.UtcNow.AddSeconds(5))));
        await this.svc.SetEnabledAsync(writer.Id, false);
        Assert.Equal(HttpStatusCode.Forbidden, await KeepAsync(Token(DateTimeOffset.UtcNow.AddSeconds(5))));
    }

    [Fact]
    public async Task Item80_ALeaversMailGoesOnlyToAColleagueOfTheSameOrganisation_AfterSignInIsStopped()
    {
        MailboxRow leaver = await this.Person("meena", "Meena");
        MailboxRow colleague = await this.Person("ravi2", "Ravi");
        TenantRow other = await this.store.UpsertTenantAsync(new TenantRow { Slug = "other", DisplayName = "Other Hospital" });
        MailboxRow stranger = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = other.Id, LocalPart = "x", Domain = "other.in", DisplayName = "X", PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000) });
        Assert.Equal("Stop the person's sign-in first (step 1).", await this.svc.HandOverAsync(this.sgh, colleague.Id, leaver, colleague.Id, "read"));
        await this.svc.NoteLeaverStepAsync(this.sgh.Id, leaver.Id, s => s.Stopped = DateTimeOffset.UtcNow);
        Assert.Equal("Choose a person or shared mailbox of this organisation.", await this.svc.HandOverAsync(this.sgh, colleague.Id, leaver, stranger.Id, "read"));
        Assert.Equal("Choose someone other than the person leaving.", await this.svc.HandOverAsync(this.sgh, colleague.Id, leaver, leaver.Id, "read"));
        Assert.Null(await this.svc.HandOverAsync(this.sgh, colleague.Id, leaver, colleague.Id, "read"));
        Assert.NotNull((await this.svc.LeaverStepsAsync(this.sgh.Id, leaver.Id)).HandedOver);
    }

    [Fact]
    public async Task Sandbox_RunsEveryStep_SendsNothing_AndBouncesOnRequest()
    {
        HttpClient c = this.Client();
        HttpResponseMessage sent = await Send(c, this.testKey, Body(PriyaSandboxBounce));
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await sent.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("sandbox").GetBoolean());
        Guid id = body.RootElement.GetProperty("id").GetGuid();
        Assert.Empty(this.store.Outbound);
        Assert.Equal("bounced", await WaitForStatusAsync(c, this.testKey, id, "bounced"));
        HttpResponseMessage clean = await Send(c, this.testKey, Body(Outside));
        Guid cleanId = JsonDocument.Parse(await clean.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        Assert.Equal("delivered", await WaitForStatusAsync(c, this.testKey, cleanId, "delivered"));
        Assert.Empty(this.store.Outbound);

        // A sandbox key never reaches live data: no live sends, no inbox, no do-not-send list, no webhook changes.
        HttpResponseMessage live = await Send(c, this.liveKey, Body(Outside));
        Guid liveId = JsonDocument.Parse(await live.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await Get(c, this.testKey, "messages/" + liveId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(c, this.liveKey, "messages/" + cleanId)).StatusCode);
        string listed = await (await Get(c, this.testKey, "messages")).Content.ReadAsStringAsync();
        Assert.Contains(cleanId.ToString(), listed, StringComparison.Ordinal);
        Assert.DoesNotContain(liveId.ToString(), listed, StringComparison.Ordinal);
        Assert.Equal("sandbox_key", await ErrorOf(await Get(c, this.testKey, "suppressions")));
        Assert.Equal("sandbox_key", await ErrorOf(await Post(c, this.testKey, "suppressions", "{\"address\":\"x@gmail.test\"}")));
        Assert.Equal("sandbox_key", await ErrorOf(await Post(c, this.testKey, "webhooks", "{\"url\":\"https://example.com/h\"}")));
    }

    [Fact]
    public async Task Suppressions_HoldBackMail_AndOnlyHardBouncesAndManualEntriesMayBeRemovedByTheApplication()
    {
        HttpClient c = this.Client();
        HttpResponseMessage added = await Post(c, this.liveKey, "suppressions", "{\"address\":\"Gone@Gmail.test\",\"note\":\"left the hospital\"}");
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(c, this.liveKey, "suppressions", "{\"address\":\"gone@gmail.test\"}")).StatusCode);
        using (JsonDocument one = JsonDocument.Parse(await (await Get(c, this.liveKey, "suppressions?address=gone@gmail.test")).Content.ReadAsStringAsync()))
        {
            Assert.True(one.RootElement.GetProperty("suppressed").GetBoolean());
            Assert.Equal("manual", one.RootElement.GetProperty("reason").GetString());
        }
        HttpResponseMessage held = await Send(c, this.liveKey, Body(Gone));
        Assert.Equal(HttpStatusCode.Conflict, held.StatusCode);
        Assert.Equal("suppressed", await ErrorOf(held));
        HttpResponseMessage partly = await Send(c, this.liveKey, Body(GonePriya));
        Assert.Equal(HttpStatusCode.Accepted, partly.StatusCode);
        Assert.Equal("gone@gmail.test", JsonDocument.Parse(await partly.Content.ReadAsStringAsync()).RootElement.GetProperty("suppressed")[0].GetString());
        Assert.Equal("priya@gmail.test", Assert.Single(this.store.Outbound).EnvelopeTo);

        await AppSuppressions.AddAsync(this.store, this.store, this.sgh.Id, this.appId, "angry@gmail.test", "complaint", "Marked as spam", "anjal", null, DateTimeOffset.UtcNow);
        HttpResponseMessage notYours = await Delete(c, this.liveKey, "suppressions/angry%40gmail.test");
        Assert.Equal(HttpStatusCode.Forbidden, notYours.StatusCode);
        Assert.Equal("removal_not_allowed", await ErrorOf(notYours));
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(c, this.liveKey, "suppressions/gone%40gmail.test")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Delete(c, this.liveKey, "suppressions/gone%40gmail.test")).StatusCode);
        using JsonDocument list = JsonDocument.Parse(await (await Get(c, this.liveKey, "suppressions?reason=complaint")).Content.ReadAsStringAsync());
        Assert.Equal("angry@gmail.test", Assert.Single(list.RootElement.GetProperty("suppressions").EnumerateArray()).GetProperty("address").GetString());
        List<string> types = (await this.store.ListAppEventsAsync(this.appId, 0, 100)).Select(e => e.Type).ToList();
        Assert.Contains("suppression.added", types);
        Assert.Contains("suppression.removed", types);
        Assert.Contains("message.suppressed", types);
    }

    [Fact]
    public async Task Batch_EachMessageOnItsOwn_OneBadAddressStopsNothing_AndTheWholeBatchReplays()
    {
        HttpClient c = this.Client();
        string batch = "{\"messages\":[" + Body(Outside) + "," + Body(NotAnAddress) + "," + Body(Ravi) + "]}";
        HttpResponseMessage res = await Post(c, this.liveKey, "messages/email/batch", batch, "batch-1");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        JsonElement[] results = doc.RootElement.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(3, results.Length);
        Assert.True(results[0].TryGetProperty("id", out _));
        Assert.Equal("invalid_request", results[1].GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(2, results[2].GetProperty("index").GetInt32());
        Assert.Equal(2, this.store.Outbound.Count);
        HttpResponseMessage replay = await Post(c, this.liveKey, "messages/email/batch", batch, "batch-1");
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(2, this.store.Outbound.Count);
        Assert.Equal("invalid_request", await ErrorOf(await Post(c, this.liveKey, "messages/email/batch", "{\"messages\":[]}")));
    }

    [Fact]
    public async Task Templates_FillInTheLanguageAsked_EscapeValuesInHtml_AndRefuseAMissingValueOrAnUnknownTemplate()
    {
        HttpClient c = this.Client();
        Assert.NotNull(await this.svc.SaveAppTemplateAsync(this.sgh.Id, "signin-code", "ta", new AppTemplateText { Subject = "x", Text = "y" }, "arun@sgh.in"));
        Assert.Null(await this.svc.SaveAppTemplateAsync(this.sgh.Id, "signin-code", "en", new AppTemplateText { Subject = "Your code {{code}}", Text = "Code {{code}} for {{minutes}} minutes.", Html = "<p>Code <b>{{code}}</b> for {{minutes}}</p>" }, "arun@sgh.in"));
        Assert.Null(await this.svc.SaveAppTemplateAsync(this.sgh.Id, "signin-code", "ta", new AppTemplateText { Subject = "உங்கள் குறியீடு {{code}}", Text = "குறியீடு {{code}}", Html = string.Empty }, "arun@sgh.in"));
        AppTemplate t = Assert.Single(await this.svc.AppTemplatesAsync(this.sgh.Id));
        Assert.Equal(2, t.Current!.Version);
        Assert.False(t.Current.Languages["ta"].Checked);
        Assert.Equal(CodeMinutes, MailboxService.AppTemplatePlaceholders(t));

        string body = "{\"from\":{\"email\":\"no-reply@sgh.in\"},\"to\":{\"email\":\"priya@gmail.test\"},\"template\":{\"name\":\"signin-code\",\"values\":{\"code\":\"<482 113>\",\"minutes\":\"10\"}}}";
        HttpResponseMessage sent = await Send(c, this.liveKey, body);
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        string wire = Encoding.UTF8.GetString(Assert.Single(this.store.Outbound).RawBytes);
        Assert.Contains("Subject: Your code <482 113>", wire, StringComparison.Ordinal);
        Assert.Contains("&lt;482 113&gt;", HtmlOf(Assert.Single(this.store.Outbound).RawBytes), StringComparison.Ordinal);
        Guid id = JsonDocument.Parse(await sent.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using JsonDocument status = JsonDocument.Parse(await (await Get(c, this.liveKey, "messages/" + id)).Content.ReadAsStringAsync());
        Assert.Equal(2, status.RootElement.GetProperty("template").GetProperty("version").GetInt32());

        HttpResponseMessage missing = await Send(c, this.liveKey, body.Replace(",\"minutes\":\"10\"", string.Empty, StringComparison.Ordinal));
        Assert.Equal((HttpStatusCode)422, missing.StatusCode);
        using JsonDocument why = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
        Assert.Equal("template_value_missing", why.RootElement.GetProperty("error").GetString());
        Assert.Equal("minutes", why.RootElement.GetProperty("placeholder").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c, this.liveKey, body.Replace("signin-code", "nothing", StringComparison.Ordinal))).StatusCode);
        Assert.Equal("invalid_request", await ErrorOf(await Send(c, this.liveKey, body.Replace("\"template\"", "\"subject\":\"x\",\"template\"", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task Signing_WhenRequired_EachFailureHasItsCode_P1363AndDerAreAccepted_AndAReplayIsRefused()
    {
        HttpClient c = this.Client();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = key.ExportSubjectPublicKeyInfoPem();
        Assert.NotNull(await this.svc.AddSigningKeyAsync(this.sgh, this.appId, key.ExportPkcs8PrivateKeyPem()));
        using (RSA rsa = RSA.Create(2048))
        {
            Assert.NotNull(await this.svc.AddSigningKeyAsync(this.sgh, this.appId, rsa.ExportSubjectPublicKeyInfoPem()));
        }
        Assert.Null(await this.svc.AddSigningKeyAsync(this.sgh, this.appId, pem));
        string keyId = (await this.svc.AppOfAsync(this.sgh.Id, this.appId))!.SigningKeys.Single().Id;
        Assert.StartsWith("sk_", keyId, StringComparison.Ordinal);
        Assert.Equal(15, keyId.Length);
        Assert.Null(await this.svc.SaveAppApiSettingsAsync(this.sgh, this.appId, string.Empty, 200, 1000, string.Empty, signingRequired: true));

        Assert.Equal("signature_required", await ErrorOf(await Get(c, this.liveKey, "limits")));
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        HttpResponseMessage good = await this.Signed(c, HttpMethod.Get, "limits", null, key, keyId, now, der: false);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await this.Signed(c, HttpMethod.Post, "messages/email", Body(Outside), key, keyId, now, der: true)).StatusCode);

        // The same signed request again is a replay.
        using (HttpRequestMessage once = SignedRequest(HttpMethod.Get, "limits", null, key, keyId, now + 1, der: false))
        using (HttpRequestMessage twice = SignedRequest(HttpMethod.Get, "limits", null, key, keyId, now + 1, der: false))
        {
            twice.Headers.Remove("Anjal-Signature");
            twice.Headers.Add("Anjal-Signature", once.Headers.GetValues("Anjal-Signature").Single());
            once.Headers.Add("X-Api-Key", this.liveKey);
            twice.Headers.Add("X-Api-Key", this.liveKey);
            Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(once)).StatusCode);
            HttpResponseMessage replayed = await c.SendAsync(twice);
            Assert.Equal("signature_replayed", await ErrorOf(replayed));

            // The same signature written another way - padded, or as n - s - is the same request.
            string seen = once.Headers.GetValues("Anjal-Signature").Single();
            byte[] raw = WebAuthn.FromBase64Url(seen)!;
            var order = System.Numerics.BigInteger.Parse("0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
            System.Numerics.BigInteger flipped = order - new System.Numerics.BigInteger(raw.AsSpan(32), isUnsigned: true, isBigEndian: true);
            byte[] other = new byte[64];
            raw.AsSpan(0, 32).CopyTo(other);
            byte[] fs = flipped.ToByteArray(isUnsigned: true, isBigEndian: true);
            fs.CopyTo(other.AsSpan(64 - fs.Length));
            foreach (string variant in new[] { seen + "=", WebAuthn.ToBase64Url(other) })
            {
                using HttpRequestMessage again = SignedRequest(HttpMethod.Get, "limits", null, key, keyId, now + 1, der: false);
                again.Headers.Remove("Anjal-Signature");
                again.Headers.Add("Anjal-Signature", variant);
                again.Headers.Add("X-Api-Key", this.liveKey);
                Assert.Equal("signature_replayed", await ErrorOf(await c.SendAsync(again)));
            }
        }
        HttpResponseMessage old = await this.Signed(c, HttpMethod.Get, "limits", null, key, keyId, now - 600, der: false);
        Assert.Equal("timestamp_out_of_window", await ErrorOf(old));
        using (JsonDocument o = JsonDocument.Parse(await old.Content.ReadAsStringAsync()))
        {
            Assert.True(o.RootElement.GetProperty("serverTime").GetInt64() >= now);
        }
        using (ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            Assert.Equal("signature_invalid", await ErrorOf(await this.Signed(c, HttpMethod.Get, "limits", null, other, keyId, now + 2, der: false)));
        }
        // A different body than the one signed is a different request.
        using (HttpRequestMessage changed = SignedRequest(HttpMethod.Post, "messages/email", Body(Outside), key, keyId, now + 3, der: false))
        {
            changed.Content = new StringContent(Body(Ravi), Encoding.UTF8, "application/json");
            changed.Headers.Add("X-Api-Key", this.liveKey);
            Assert.Equal("signature_invalid", await ErrorOf(await c.SendAsync(changed)));
        }
        Assert.Null(await this.svc.RetireSigningKeyAsync(this.sgh, this.appId, keyId));
        Assert.Equal("signing_key_retired", await ErrorOf(await this.Signed(c, HttpMethod.Get, "limits", null, key, keyId, now + 4, der: false)));
    }

    [Fact]
    public async Task Inbox_MailIsEncryptedToTheApplicationsKey_ListedWithoutSubject_CollectedThenAcknowledged()
    {
        HttpClient c = this.Client();
        using RSA appKey = RSA.Create(3072);
        using ECDsa sign = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal("Upload the application's encryption key first: its mail is always encrypted to it.", (await this.svc.AddInboxAddressAsync(this.sgh, "arun@sgh.in", this.appId, "reports@sgh.in", false)).Error);
        using (RSA small = RSA.Create(2048))
        {
            Assert.Contains("3072", await this.svc.AddEncryptionKeyAsync(this.sgh, this.appId, small.ExportSubjectPublicKeyInfoPem()) ?? string.Empty, StringComparison.Ordinal);
        }
        Assert.Null(await this.svc.AddEncryptionKeyAsync(this.sgh, this.appId, appKey.ExportSubjectPublicKeyInfoPem()));
        Assert.Null(await this.svc.AddSigningKeyAsync(this.sgh, this.appId, sign.ExportSubjectPublicKeyInfoPem()));
        string keyId = (await this.svc.AppOfAsync(this.sgh.Id, this.appId))!.SigningKeys.Single().Id;
        (string? error, MailboxRow? holder) = await this.svc.AddInboxAddressAsync(this.sgh, "arun@sgh.in", this.appId, "reports@sgh.in", false);
        Assert.Null(error);
        Assert.Null(holder);

        // A send of the application, so that a reply to it is matched (DES-14 §16).
        HttpResponseMessage sent = await this.Signed(c, HttpMethod.Post, "messages/email", Body(Outside), sign, keyId, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), der: false);
        using JsonDocument sentBody = JsonDocument.Parse(await sent.Content.ReadAsStringAsync());
        string messageId = sentBody.RootElement.GetProperty("messageId").GetString()!;
        Guid sendId = sentBody.RootElement.GetProperty("id").GetGuid();

        string raw = "From: Lab <results@lab.test>\r\nTo: reports@sgh.in\r\nSubject: Culture report, bed 12\r\nIn-Reply-To: <" + messageId + ">\r\nAuthentication-Results: anjal.localhost; spf=pass smtp.mailfrom=lab.test; dkim=pass header.d=lab.test; dmarc=pass\r\n\r\nNo growth after 48 hours.\r\n";
        DeliveryResult result = await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext { EnvelopeFrom = "results@lab.test", EnvelopeTo = Reports, RawBytes = Encoding.UTF8.GetBytes(raw), TransportTls = "TLS 1.3" });
        Assert.Equal(DeliveryOutcome.Accepted, result.Outcome);
        MessageRow stored = Assert.Single(this.store.MailboxMessages, m => m.ToHeader == "reports@sgh.in");
        Assert.Equal(string.Empty, stored.Subject);
        Assert.DoesNotContain(await this.store.ListMailboxesAsync(this.sgh.Id), p => p.Kind == MailboxRow.ApplicationKind);

        Assert.Equal("signature_required", await ErrorOf(await Get(c, this.liveKey, "inbox")));
        HttpResponseMessage listed = await this.Signed(c, HttpMethod.Get, "inbox?limit=10", null, sign, keyId, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), der: false);
        string listText = await listed.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.DoesNotContain("Culture report", listText, StringComparison.Ordinal);
        using JsonDocument list = JsonDocument.Parse(listText);
        JsonElement item = Assert.Single(list.RootElement.GetProperty("mails").EnumerateArray());
        Assert.Equal("results@lab.test", item.GetProperty("from").GetString());
        Assert.Equal("reports@sgh.in", item.GetProperty("to").GetString());
        Assert.Equal("pass", item.GetProperty("auth").GetProperty("dmarc").GetString());
        Assert.Equal(sendId.ToString(), item.GetProperty("inReplyToSend").GetString());
        Guid inboxId = item.GetProperty("id").GetGuid();

        HttpResponseMessage got = await this.Signed(c, HttpMethod.Get, "inbox/" + inboxId, null, sign, keyId, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1, der: false);
        byte[] envelope = await got.Content.ReadAsByteArrayAsync();
        Assert.DoesNotContain("Culture report", Encoding.UTF8.GetString(envelope), StringComparison.Ordinal);
        using (JsonDocument env = JsonDocument.Parse(envelope))
        {
            Assert.Equal(AppEnvelope.Algorithm, env.RootElement.GetProperty("alg").GetString());
            Assert.Equal(1, env.RootElement.GetProperty("v").GetInt32());
        }
        Assert.Contains("Subject: Culture report, bed 12", Encoding.UTF8.GetString(AppEnvelope.Open(envelope, appKey)), StringComparison.Ordinal);
        HttpResponseMessage rawToo = await this.Signed(c, HttpMethod.Get, "inbox/" + inboxId + "/raw", null, sign, keyId, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2, der: false);
        Assert.Equal(envelope, await rawToo.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await this.Signed(c, HttpMethod.Post, "inbox/" + inboxId + "/ack", null, sign, keyId, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3, der: false)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await this.Signed(c, HttpMethod.Post, "inbox/" + inboxId + "/ack", null, sign, keyId, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 4, der: false)).StatusCode);
        HttpResponseMessage many = await this.Signed(c, HttpMethod.Post, "inbox/ack", "{\"ids\":[\"" + inboxId + "\",\"" + Guid.NewGuid() + "\"]}", sign, keyId, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5, der: false);
        using (JsonDocument acks = JsonDocument.Parse(await many.Content.ReadAsStringAsync()))
        {
            Assert.True(acks.RootElement.GetProperty("results")[0].GetProperty("acknowledged").GetBoolean());
            Assert.False(acks.RootElement.GetProperty("results")[1].GetProperty("acknowledged").GetBoolean());
        }
        using (JsonDocument empty = JsonDocument.Parse(await (await this.Signed(c, HttpMethod.Get, "inbox", null, sign, keyId, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 6, der: false)).Content.ReadAsStringAsync()))
        {
            Assert.Equal(0, empty.RootElement.GetProperty("mails").GetArrayLength());
        }
        List<string> types = (await this.store.ListAppEventsAsync(this.appId, 0, 100)).Select(e => e.Type).ToList();
        Assert.Contains("inbox.received", types);
        Assert.Contains("message.replied", types);
        AppInboxView view = await this.svc.AppInboxAsync(this.sgh, (await this.svc.AppOfAsync(this.sgh.Id, this.appId))!);
        Assert.Equal(0, view.Waiting);
        Assert.Equal("reports@sgh.in", Assert.Single(view.Addresses).Address);

        // Acknowledged mail is cleared 30 days later, not before.
        Assert.DoesNotContain(await this.svc.AppApiDailyAsync(this.root, DateTimeOffset.UtcNow.AddDays(1)), l => l.Contains("acknowledged mails cleared", StringComparison.Ordinal));
        IReadOnlyList<string> later = await this.svc.AppApiDailyAsync(this.root, DateTimeOffset.UtcNow.AddDays(31));
        Assert.Contains(later, l => l.Contains("1 acknowledged mails cleared", StringComparison.Ordinal));
        Assert.DoesNotContain(this.store.MailboxMessages, m => m.Id == inboxId);
    }

    [Fact]
    public async Task ASharedAddress_GoesToBoth_TheHolderSeesTheLine_AndAFullInboxStillLetsTheHolderHaveIt()
    {
        using RSA appKey = RSA.Create(3072);
        Assert.Null(await this.svc.AddEncryptionKeyAsync(this.sgh, this.appId, appKey.ExportSubjectPublicKeyInfoPem()));
        (string? error, MailboxRow? holder) = await this.svc.AddInboxAddressAsync(this.sgh, "arun@sgh.in", this.appId, "meera@sgh.in", false);
        Assert.Null(error);
        Assert.Equal(this.meera.Id, holder!.Id);
        Assert.Null((await this.svc.AddInboxAddressAsync(this.sgh, "arun@sgh.in", this.appId, "meera@sgh.in", true)).Error);
        Assert.Contains((await this.store.ListAuditAsync(10)), a => a.Action == "org.app.inbox-shared");

        string raw = "From: Lab <results@lab.test>\r\nTo: meera@sgh.in\r\nX-Anjal-Also-Delivered-To: forged\r\nSubject: Shared\r\n\r\nx\r\n";
        DeliveryResult result = await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext { EnvelopeFrom = "results@lab.test", EnvelopeTo = MeeraOnly, RawBytes = Encoding.UTF8.GetBytes(raw) });
        Assert.Equal(DeliveryOutcome.Accepted, result.Outcome);
        MessageRow personal = Assert.Single(this.store.MailboxMessages, m => m.MailboxId == this.meera.Id);
        OrgApp app = (await this.svc.AppOfAsync(this.sgh.Id, this.appId))!;
        Assert.Single(this.store.MailboxMessages, m => m.MailboxId == app.InboxMailboxId);
        FolderRow inbox = (await this.store.ListFoldersAsync(this.meera.Id)).Single(f => f.Id == personal.FolderId);
        string copy = Encoding.UTF8.GetString((await this.maildir.ReadAsync("sgh", "meera@sgh.in", inbox.Name, personal.MaildirFile))!);
        Assert.Contains(MailboxSink.AlsoDeliveredHeader + ": Hospital information system", copy, StringComparison.Ordinal);
        Assert.DoesNotContain("forged", copy, StringComparison.Ordinal);

        // The application's inbox full: the holder still has the mail; the application's copy waits for nobody.
        Assert.Null(await this.svc.ChangeAppAsync(this.sgh.Id, this.appId, a => { a.InboxQuotaBytes = 1; return null; }));
        DeliveryResult second = await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext { EnvelopeFrom = "results@lab.test", EnvelopeTo = MeeraOnly, RawBytes = Encoding.UTF8.GetBytes(raw) });
        Assert.Equal(DeliveryOutcome.Accepted, second.Outcome);
        Assert.Equal(2, this.store.MailboxMessages.Count(m => m.MailboxId == this.meera.Id));
        Assert.Single(this.store.MailboxMessages, m => m.MailboxId == app.InboxMailboxId);

        // An address of the application alone, full: "try later".
        Assert.Null((await this.svc.AddInboxAddressAsync(this.sgh, "arun@sgh.in", this.appId, "reports@sgh.in", false)).Error);
        DeliveryResult full = await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext { EnvelopeFrom = "results@lab.test", EnvelopeTo = Reports, RawBytes = Encoding.UTF8.GetBytes(raw) });
        Assert.Equal(DeliveryOutcome.TransientFailure, full.Outcome);

        // Stop sharing: the address is the holder's alone again.
        Assert.Null(await this.svc.RemoveInboxAddressAsync(this.sgh, "arun@sgh.in", this.appId, "meera@sgh.in"));
        Assert.Null(await this.store.GetAppInboxAddressAsync("meera@sgh.in"));
        Assert.Contains((await this.store.ListAuditAsync(10)), a => a.Action == "org.app.inbox-unshared");
        Assert.NotNull(await this.svc.RemoveEncryptionKeyAsync(this.sgh, this.appId, app.EncryptionKeys[0].Fingerprint));
    }

    [Fact]
    public async Task ReturnPath_TakesDeliveryAndFeedbackReportsFromOutside_AndNothingElse()
    {
        HttpClient c = this.Client();
        HttpResponseMessage sent = await Send(c, this.liveKey, Body(PriyaRavi));
        Guid id = JsonDocument.Parse(await sent.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        string bounce = AppBounceAddress.For(id, "bounce.anjal.localhost");
        var sink = new MailboxSink(this.store, this.maildir);
        Assert.True(await sink.IsApplicationAddressAsync(bounce));
        Assert.False(await sink.IsApplicationAddressAsync(AppBounceAddress.For(Guid.NewGuid(), "bounce.anjal.localhost")));

        string dsn = "From: MAILER-DAEMON@gmail.test\r\nTo: " + bounce + "\r\nSubject: Undelivered\r\nMIME-Version: 1.0\r\nContent-Type: multipart/report; report-type=delivery-status; boundary=\"b\"\r\n\r\n"
            + "--b\r\nContent-Type: text/plain\r\n\r\nSorry.\r\n"
            + "--b\r\nContent-Type: message/delivery-status\r\n\r\nReporting-MTA: dns; mx.gmail.test\r\n\r\nFinal-Recipient: rfc822; priya@gmail.test\r\nAction: failed\r\nStatus: 5.1.1\r\nDiagnostic-Code: smtp; 550 5.1.1 user unknown\r\n\r\nFinal-Recipient: rfc822; ravi@gmail.test\r\nAction: delayed\r\nStatus: 4.2.2\r\n\r\n"
            + "--b--\r\n";
        DeliveryResult taken = await sink.DeliverAsync(new DeliveryContext { EnvelopeFrom = string.Empty, EnvelopeTo = new[] { bounce }, RawBytes = Encoding.UTF8.GetBytes(dsn) });
        Assert.Equal(DeliveryOutcome.Accepted, taken.Outcome);
        List<AppEvent> events = (await this.store.ListAppEventsAsync(this.appId, 0, 100)).ToList();
        AppEvent hard = events.Single(e => e.Type == "message.bounced");
        Assert.Contains("\"source\":\"dsn\"", hard.Data, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"hard\"", hard.Data, StringComparison.Ordinal);
        Assert.Contains(events, e => e.Type == "message.deferred" && e.Data.Contains("ravi@gmail.test", StringComparison.Ordinal));
        Assert.Equal("hard_bounce", (await AppSuppressions.FindAsync(this.store, this.sgh.Id, this.appId, "priya@gmail.test"))!.Code);
        Assert.Null(await AppSuppressions.FindAsync(this.store, this.sgh.Id, this.appId, "ravi@gmail.test"));

        string arf = "From: abuse@yahoo.test\r\nTo: " + bounce + "\r\nSubject: Complaint\r\nMIME-Version: 1.0\r\nContent-Type: multipart/report; report-type=feedback-report; boundary=\"f\"\r\n\r\n"
            + "--f\r\nContent-Type: text/plain\r\n\r\nA complaint.\r\n--f\r\nContent-Type: message/feedback-report\r\n\r\nFeedback-Type: abuse\r\nOriginal-Rcpt-To: ravi@gmail.test\r\n\r\n--f--\r\n";
        Assert.Equal(DeliveryOutcome.Accepted, (await sink.DeliverAsync(new DeliveryContext { EnvelopeFrom = string.Empty, EnvelopeTo = new[] { bounce }, RawBytes = Encoding.UTF8.GetBytes(arf) })).Outcome);
        Assert.Contains((await this.store.ListAppEventsAsync(this.appId, 0, 100)), e => e.Type == "message.complained");
        Assert.Equal("complaint", (await AppSuppressions.FindAsync(this.store, this.sgh.Id, this.appId, "ravi@gmail.test"))!.Code);

        // A report naming someone the send did not go to is ignored: anyone can write to the return path.
        string forged = arf.Replace("ravi@gmail.test", "victim@gmail.test", StringComparison.Ordinal);
        Assert.Equal(DeliveryOutcome.Accepted, (await sink.DeliverAsync(new DeliveryContext { EnvelopeFrom = string.Empty, EnvelopeTo = new[] { bounce }, RawBytes = Encoding.UTF8.GetBytes(forged) })).Outcome);
        string forgedDsn = dsn.Replace("priya@gmail.test", "victim2@gmail.test", StringComparison.Ordinal);
        Assert.Equal(DeliveryOutcome.Accepted, (await sink.DeliverAsync(new DeliveryContext { EnvelopeFrom = string.Empty, EnvelopeTo = new[] { bounce }, RawBytes = Encoding.UTF8.GetBytes(forgedDsn) })).Outcome);
        Assert.Null(await AppSuppressions.FindAsync(this.store, this.sgh.Id, this.appId, "victim@gmail.test"));
        Assert.Null(await AppSuppressions.FindAsync(this.store, this.sgh.Id, this.appId, "victim2@gmail.test"));

        DeliveryResult notReport = await sink.DeliverAsync(new DeliveryContext { EnvelopeFrom = "x@gmail.test", EnvelopeTo = new[] { bounce }, RawBytes = "From: x@gmail.test\r\nSubject: hello\r\n\r\nhi\r\n"u8.ToArray() });
        Assert.Equal(DeliveryOutcome.PermanentFailure, notReport.Outcome);
        DeliveryResult inside = await sink.DeliverAsync(new DeliveryContext { EnvelopeFrom = "arun@sgh.in", EnvelopeTo = new[] { bounce }, RawBytes = Encoding.UTF8.GetBytes(dsn), AuthenticatedUser = "arun@sgh.in" });
        Assert.Equal(DeliveryOutcome.PermanentFailure, inside.Outcome);
    }

    [Fact]
    public async Task TheWorkersFailure_PutsAHardBounceOnTheList_AndHoldsBackLaterRows()
    {
        HttpClient c = this.Client();
        HttpResponseMessage sent = await Send(c, this.liveKey, Body(Outside));
        OutboundMessage row = Assert.Single(this.store.Outbound);
        Assert.Null(await AppSuppressions.NoteFinalFailureAsync(this.store, this.store, row, "451 4.2.2 full", permanent: false, DateTimeOffset.UtcNow));
        DoNotSendEntry? added = await AppSuppressions.NoteFinalFailureAsync(this.store, this.store, row, "550 5.1.1 user unknown", permanent: true, DateTimeOffset.UtcNow);
        Assert.Equal("Mailbox gone", added!.Reason);
        Assert.Equal(this.appId, added.App);
        Assert.Equal("Mailbox gone", await AppSuppressions.CheckRowAsync(this.store, this.store, row));
        Assert.Equal(HttpStatusCode.Conflict, (await Send(c, this.liveKey, Body(Outside))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
    }

    [Fact]
    public async Task Webhooks_AreSignedPostedAndRetried_TheTestEventGoesToOne_AndAPrivateAddressNeedsTheOperator()
    {
        HttpClient c = this.Client();
        var received = new List<(string Signature, string EventId, string Body)>();
        int answer = 200;
        WebApplication hookApp = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder().Build();
        hookApp.Urls.Add("http://127.0.0.1:0");
        hookApp.MapPost("/hook", async (Microsoft.AspNetCore.Http.HttpContext http) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            string body = await reader.ReadToEndAsync();
            lock (received)
            {
                received.Add((http.Request.Headers["Anjal-Signature"].ToString(), http.Request.Headers["Anjal-Event-Id"].ToString(), body));
            }
            http.Response.StatusCode = answer;
        });
        await hookApp.StartAsync();
        try
        {
            string hookUrl = hookApp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/hook";
            HttpResponseMessage refused = await Post(c, this.liveKey, "webhooks", "{\"url\":\"" + hookUrl + "\"}");
            Assert.Equal((HttpStatusCode)422, refused.StatusCode);
            await this.AllowPrivateAsync();
            Assert.Equal("invalid_request", await ErrorOf(await Post(c, this.liveKey, "webhooks", "{\"url\":\"" + hookUrl + "\",\"events\":[\"message.exploded\"]}")));
            HttpResponseMessage added = await Post(c, this.liveKey, "webhooks", "{\"url\":\"" + hookUrl + "\",\"events\":[\"message.queued\",\"webhook.test\"]}");
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
            using JsonDocument hook = JsonDocument.Parse(await added.Content.ReadAsStringAsync());
            string secret = hook.RootElement.GetProperty("secret").GetString()!;
            string hookId = hook.RootElement.GetProperty("id").GetString()!;
            using (JsonDocument listed = JsonDocument.Parse(await (await Get(c, this.liveKey, "webhooks")).Content.ReadAsStringAsync()))
            {
                Assert.DoesNotContain(secret, listed.RootElement.GetRawText(), StringComparison.Ordinal);
            }

            AppWebhookWorker worker = this.app.Services.GetRequiredService<AppWebhookWorker>();
            await Send(c, this.liveKey, Body(Outside));
            await worker.RunOnceAsync(DateTimeOffset.UtcNow);
            (string signature, string eventId, string body) = Assert.Single(received);
            long t = long.Parse(signature.Split(',')[0][2..], System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(AppWebhookWorker.Signature(secret, t, body), signature);
            using (JsonDocument e = JsonDocument.Parse(body))
            {
                Assert.Equal("message.queued", e.RootElement.GetProperty("type").GetString());
                Assert.Equal(eventId, e.RootElement.GetProperty("id").GetString());
            }

            // A failing answer is tried again later; the schedule is 1, 5, 30 minutes, then hourly for 24 hours.
            answer = 500;
            Assert.Equal(HttpStatusCode.Accepted, (await Post(c, this.liveKey, "webhooks/" + hookId + "/test", string.Empty)).StatusCode);
            await worker.RunOnceAsync(DateTimeOffset.UtcNow);
            AppWebhookDelivery failed = (await this.store.ListAppWebhookDeliveriesAsync(this.appId, 10))[0];
            Assert.Equal("pending", failed.Status);
            Assert.Equal(500, failed.Code);
            Assert.True(failed.NextAt > DateTimeOffset.UtcNow.AddSeconds(50));
            DateTimeOffset first = DateTimeOffset.UtcNow;
            Assert.Equal(first.AddMinutes(5), AppWebhookWorker.NextAttempt(2, first, first));
            Assert.Equal(first.AddHours(1), AppWebhookWorker.NextAttempt(4, first, first));
            Assert.Null(AppWebhookWorker.NextAttempt(9, first, first.AddHours(23.5)));
            answer = 204;
            await worker.RunOnceAsync(DateTimeOffset.UtcNow.AddMinutes(2));
            Assert.Equal("delivered", (await this.store.ListAppWebhookDeliveriesAsync(this.appId, 10)).First(d => d.Id == failed.Id).Status);
            Assert.Equal(HttpStatusCode.NoContent, (await Delete(c, this.liveKey, "webhooks/" + hookId)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Delete(c, this.liveKey, "webhooks/" + hookId)).StatusCode);
        }
        finally
        {
            await hookApp.StopAsync();
            await hookApp.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheApiDescription_IsServedWithoutAKey_AndNamesEveryEndpoint()
    {
        HttpClient c = this.Client();
        HttpResponseMessage res = await c.GetAsync("api/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        string json = await res.Content.ReadAsStringAsync();
        Assert.Equal("3.1.0", JsonDocument.Parse(json).RootElement.GetProperty("openapi").GetString());
        IReadOnlyList<string> described = AppOpenApi.Operations(json);
        var mapped = this.app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith(AppApiRoutes.Base, StringComparison.Ordinal) == true)
            .SelectMany(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods.Select(m => m + " " + e.RoutePattern.RawText))
            .ToList();
        Assert.Equal(AppApiRoutes.All.Count, mapped.Count);
        foreach (string endpoint in mapped)
        {
            Assert.Contains(endpoint, described);
        }
    }

    [Fact]
    public async Task Health_SaysWhetherTheApplicationCanSend_AndTheReturnPathFollowsTheDomainsRecords()
    {
        HttpClient c = this.Client();
        HttpResponseMessage noDkim = await Get(c, this.liveKey, "health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, noDkim.StatusCode);
        using (JsonDocument h = JsonDocument.Parse(await noDkim.Content.ReadAsStringAsync()))
        {
            Assert.False(h.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("no key", h.RootElement.GetProperty("domains")[0].GetProperty("dkim").GetString());
        }
        using (RSA dkim = RSA.Create(2048))
        {
            await this.store.UpsertDkimKeyAsync(new DkimKeyRow { Domain = "sgh.in", Selector = "default", PrivateKeyPem = dkim.ExportPkcs8PrivateKeyPem() });
        }
        Assert.Equal(HttpStatusCode.OK, (await Get(c, this.liveKey, "health")).StatusCode);

        Assert.Equal(("bounce.anjal.localhost", 3), await this.svc.AppReturnDomainAsync(this.sgh.Id, "sgh.in"));
        var check = new DnsCheckRecord { At = DateTimeOffset.UtcNow };
        check.Domains["sgh.in"] = new Dictionary<string, string> { ["MX"] = "different", ["Return path"] = "pass" };
        await this.svc.WriteTenantDocumentAsync(this.sgh.Id, MailboxService.DnsCheckKind, check);
        Assert.Equal(("bounce.sgh.in", 2), await this.svc.AppReturnDomainAsync(this.sgh.Id, "sgh.in"));
        check.Domains["sgh.in"]["MX"] = "pass";
        await this.svc.WriteTenantDocumentAsync(this.sgh.Id, MailboxService.DnsCheckKind, check);
        Assert.Equal(("sgh.in", 1), await this.svc.AppReturnDomainAsync(this.sgh.Id, "sgh.in"));
    }

    [Fact]
    public async Task PatientData_MakesSigningRequired_AndTheLogKeepsEachSendsRecipientDomains()
    {
        HttpClient c = this.Client();
        Assert.NotNull(await this.svc.SetPatientDataAsync(this.sgh, this.appId, true, "x", "ops@anjal.co.in"));
        Assert.Null(await this.svc.SetPatientDataAsync(this.sgh, this.appId, true, "LiPi HIS sends lab results", "ops@anjal.co.in"));
        Assert.Equal("signature_required", await ErrorOf(await Send(c, this.liveKey, Body(Outside))));
        using ECDsa sign = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Null(await this.svc.AddSigningKeyAsync(this.sgh, this.appId, sign.ExportSubjectPublicKeyInfoPem()));
        string keyId = (await this.svc.AppOfAsync(this.sgh.Id, this.appId))!.SigningKeys.Single().Id;
        Assert.Equal(HttpStatusCode.Accepted, (await this.Signed(c, HttpMethod.Post, "messages/email", Body(Outside), sign, keyId, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), der: false)).StatusCode);
        IReadOnlyList<AuditEvent> log = await this.store.ListAuditAsync(20);
        Assert.Contains(log, a => a.Action == "app.send.patient-data" && a.Detail == "gmail.test");
        Assert.Contains(log, a => a.Action == "ops.app.patient-data-set");
    }

    [Fact]
    public void Pieces_TheReturnPathRoundTrips_TheEnvelopeRefusesWeakOrPrivateKeys_AndTheSigningStringIsExact()
    {
        Guid id = Guid.NewGuid();
        string address = AppBounceAddress.For(id, "Bounce.SGH.in");
        Assert.Equal(26, address.Split('@')[0].Length - AppBounceAddress.Prefix.Length);
        Assert.Equal(id, AppBounceAddress.SendOf(address.ToUpperInvariant()));
        Assert.Null(AppBounceAddress.SendOf("bounce+short@x.in"));
        using (RSA weak = RSA.Create(2048))
        {
            Assert.NotNull(AppEnvelope.Check(weak.ExportSubjectPublicKeyInfoPem(), out _));
            Assert.NotNull(AppEnvelope.Check(weak.ExportPkcs8PrivateKeyPem(), out _));
        }
        Assert.NotNull(AppEnvelope.Check("hello", out _));
        Assert.Equal("POST\n/api/v1/inbox/ack\n1793000000\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", AppApiService.SigningString("post", "/api/v1/inbox/ack", "1793000000", Array.Empty<byte>()));
        Assert.Equal("message.bounced", AppEvent.ForOutbound(OutboundStatus.Failed, "550 5.1.1 no"));
        Assert.Equal("message.failed", AppEvent.ForOutbound(OutboundStatus.Failed, "Give-up reached after 9 attempts"));
        Assert.Equal("message.suppressed", AppEvent.ForOutbound(OutboundStatus.Failed, DoNotSend.SuppressedPrefix + "x"));
        Assert.True(MailboxService.IsPrivate(IPAddress.Parse("10.1.2.3")));
        Assert.True(MailboxService.IsPrivate(IPAddress.Parse("::1")));
        Assert.False(MailboxService.IsPrivate(IPAddress.Parse("151.185.43.171")));
    }

    private static string HtmlOf(byte[] raw)
    {
        Anjal.Mime.MimeMessage m = Anjal.Mime.MimeParser.Parse(raw);
        return ((Anjal.Mime.MimeMultipart)m.Body).Parts.OfType<Anjal.Mime.MimePart>().Single(p => p.ContentType.MimeType == "text/html").GetBodyAsText();
    }

    private async Task AllowPrivateAsync()
    {
        OpsRecord ops = await this.svc.ReadTenantDocumentAsync<OpsRecord>(this.sgh.Id, MailboxService.OpsKind) ?? new OpsRecord();
        ops.AllowPrivateAddresses = true;
        await this.svc.WriteTenantDocumentAsync(this.sgh.Id, MailboxService.OpsKind, ops);
    }

    private async Task<string> StatusOf(HttpClient c, Guid id) =>
        JsonDocument.Parse(await (await Get(c, this.liveKey, "messages/" + id)).Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString()!;

    private static async Task<string> WaitForStatusAsync(HttpClient c, string key, Guid id, string want)
    {
        string status = string.Empty;
        for (int i = 0; i < 100 && status != want; i++)
        {
            await Task.Delay(30);
            status = JsonDocument.Parse(await (await Get(c, key, "messages/" + id)).Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString()!;
        }
        return status;
    }

    private static string Body(string[] to, string from = "no-reply@sgh.in", string[]? tags = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["from"] = new { email = from, name = "HIS" },
            ["to"] = to.Select(t => new { email = t }).ToArray(),
            ["subject"] = "Your sign-in code",
            ["text"] = "Your code is 482 113.",
            ["html"] = "<p>Your code is <b>482 113</b>.</p>",
            ["headers"] = new Dictionary<string, string> { ["X-His-Purpose"] = "signin" },
            ["tags"] = tags ?? CodeTag,
            ["metadata"] = new Dictionary<string, string> { ["userId"] = "u_8841" },
        });

    private static async Task<string> ErrorOf(HttpResponseMessage res)
    {
        string text = await res.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.TryGetProperty("requestId", out _), text);
        return doc.RootElement.GetProperty("error").GetString()!;
    }

    private static Task<HttpResponseMessage> Send(HttpClient c, string? key, string body, string? idempotency = null) => Post(c, key, "messages/email", body, idempotency);

    private static async Task<HttpResponseMessage> Post(HttpClient c, string? key, string path, string body, string? idempotency = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/" + path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (body.Length == 0)
        {
            req.Content = new ByteArrayContent(Array.Empty<byte>());
        }
        if (key is not null)
        {
            req.Headers.Add("X-Api-Key", key);
        }
        if (idempotency is not null)
        {
            req.Headers.Add("Idempotency-Key", idempotency);
        }
        return await c.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> Get(HttpClient c, string key, string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/v1/" + path);
        req.Headers.Add("X-Api-Key", key);
        return await c.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> Delete(HttpClient c, string key, string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, "api/v1/" + path);
        req.Headers.Add("X-Api-Key", key);
        return await c.SendAsync(req);
    }

    private async Task<HttpResponseMessage> Signed(HttpClient c, HttpMethod method, string path, string? body, ECDsa key, string keyId, long timestamp, bool der)
    {
        using HttpRequestMessage req = SignedRequest(method, path, body, key, keyId, timestamp, der);
        req.Headers.Add("X-Api-Key", this.liveKey);
        return await c.SendAsync(req);
    }

    private static HttpRequestMessage SignedRequest(HttpMethod method, string path, string? body, ECDsa key, string keyId, long timestamp, bool der)
    {
        var req = new HttpRequestMessage(method, "api/v1/" + path);
        byte[] bytes = body is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);
        if (body is not null)
        {
            req.Content = new ByteArrayContent(bytes);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        string ts = timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string toSign = AppApiService.SigningString(method.Method, "/api/v1/" + path, ts, bytes);
        byte[] sig = key.SignData(Encoding.UTF8.GetBytes(toSign), HashAlgorithmName.SHA256, der ? DSASignatureFormat.Rfc3279DerSequence : DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        req.Headers.Add("Anjal-Key-Id", keyId);
        req.Headers.Add("Anjal-Timestamp", ts);
        req.Headers.Add("Anjal-Signature", WebAuthn.ToBase64Url(sig));
        return req;
    }

    private async Task<MailboxRow> Person(string local, string name) =>
        await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = this.sgh.Id,
            LocalPart = local,
            Domain = "sgh.in",
            DisplayName = name,
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        });

    private HttpClient Client()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler) { BaseAddress = new Uri(this.baseAddress + "/") };
        this.clients.Add(client);
        return client;
    }
}
