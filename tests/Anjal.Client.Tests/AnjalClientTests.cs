using System.Net;
using System.Security.Cryptography;
using System.Text;
using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Anjal.Webmail;
using Anjal.Webmail.AppApi;
using Anjal.Webmail.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Anjal.Client.Tests;

/// <summary>
/// rc.26 (DES-14 §28.6): the client package against the real endpoints of a running webmail - send,
/// replay, status, events, batch, suppressions, the signed inbox with decryption, its errors - and its
/// retries and webhook verification on their own.
/// </summary>
public sealed class AnjalClientTests : IAsyncLifetime
{
    private static readonly string[] ResultsAddress = { "results@sgh.in" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-client-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private MailboxService svc = null!;
    private Uri baseAddress = null!;
    private TenantRow sgh = new();
    private string appId = string.Empty;
    private string key = string.Empty;

    public async Task InitializeAsync()
    {
        this.maildir = new MaildirStore(this.root, "test");
        this.sgh = await this.store.UpsertTenantAsync(new TenantRow { Slug = "sgh", DisplayName = "Sri Ganesh Hospital", PostmasterMailbox = "arun@sgh.in" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.sgh.Id, Domain = "sgh.in" });
        await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = this.sgh.Id, LocalPart = "arun", Domain = "sgh.in", DisplayName = "Arun", PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000) });
        AppWebhookWorker.Interval = TimeSpan.FromHours(1);
        this.app = Program.CreateApp(Array.Empty<string>(), this.store, this.maildir, "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
        this.svc = this.app.Services.GetRequiredService<MailboxService>();
        this.baseAddress = new Uri(this.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        (string? error, string? id, _, _) = await this.svc.AddAppAsync(this.sgh, "LiPi HIS", "Lab results", "no-reply@sgh.in", string.Empty);
        Assert.Null(error);
        this.appId = id!;
        this.key = (await this.svc.NewHttpKeyAsync(this.sgh, this.appId, false, "tests", null)).Key!;
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
    public async Task Send_Replays_Status_Events_Batch_AndSuppressions()
    {
        using var client = new AnjalClient(this.baseAddress, this.key);
        var request = Message("priya@gmail.test");
        SendResult first = await client.SendAsync(request, "order-1");
        Assert.Equal("queued", first.Status);
        Assert.False(first.Replayed);
        SendResult again = await client.SendAsync(request, "order-1");
        Assert.True(again.Replayed);
        Assert.Equal(first.Id, again.Id);
        Assert.Single(this.store.Outbound);

        MessageStatus status = await client.GetMessageAsync(first.Id);
        Assert.Equal("queued", status.Status);
        Assert.Equal("u_1", status.Metadata["userId"]);
        IReadOnlyList<AnjalEvent> events = await client.EventsAsync();
        AnjalEvent queued = Assert.Single(events);
        Assert.Equal("message.queued", queued.Type);
        Assert.Equal(first.Id, queued.SendId);
        Assert.Empty(await client.EventsAsync(queued.Seq));

        IReadOnlyList<BatchItemResult> batch = await client.SendBatchAsync(new[] { Message("ravi@gmail.test"), Message("not an address") });
        Assert.NotNull(batch[0].Id);
        Assert.Equal("invalid_request", batch[1].ErrorCode);

        Suppression added = await client.AddSuppressionAsync("gone@gmail.test", "left");
        Assert.Equal("manual", added.Reason);
        Assert.True((await client.GetSuppressionAsync("gone@gmail.test")).Suppressed);
        AnjalApiException held = await Assert.ThrowsAsync<AnjalApiException>(() => client.SendAsync(Message("gone@gmail.test")));
        Assert.Equal(HttpStatusCode.Conflict, held.Status);
        Assert.Equal("suppressed", held.Code);
        Assert.NotEmpty(held.RequestId);
        await client.RemoveSuppressionAsync("gone@gmail.test");
        Assert.False((await client.GetSuppressionAsync("gone@gmail.test")).Suppressed);

        using var stranger = new AnjalClient(this.baseAddress, "ak_live_" + new string('z', 32));
        AnjalApiException refused = await Assert.ThrowsAsync<AnjalApiException>(() => stranger.LimitsAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, refused.Status);
        Assert.Equal("invalid_key", refused.Code);
        Assert.True((await client.LimitsAsync()).GetProperty("windows").GetArrayLength() == 3);
    }

    [Fact]
    public async Task TheInbox_SignedRequests_TheEnvelopeDecryptsToTheMail_AndAcknowledgement()
    {
        using RSA appKey = RSA.Create(3072);
        using ECDsa signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Null(await this.svc.AddEncryptionKeyAsync(this.sgh, this.appId, appKey.ExportSubjectPublicKeyInfoPem()));
        Assert.Null(await this.svc.AddSigningKeyAsync(this.sgh, this.appId, signing.ExportSubjectPublicKeyInfoPem()));
        Assert.Null((await this.svc.AddInboxAddressAsync(this.sgh, "arun@sgh.in", this.appId, "results@sgh.in", false)).Error);
        string keyId = (await this.svc.AppOfAsync(this.sgh.Id, this.appId))!.SigningKeys[0].Id;

        string raw = "From: Lab <lab@lab.test>\r\nTo: results@sgh.in\r\nSubject: =?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Culture — bed 12")) + "?=\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"m\"\r\n\r\n"
            + "--m\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\nNo growth =E2=80=94 48 h.\r\n"
            + "--m\r\nContent-Type: application/pdf; name=\"report.pdf\"\r\nContent-Disposition: attachment; filename=\"report.pdf\"\r\nContent-Transfer-Encoding: base64\r\n\r\n" + Convert.ToBase64String("%PDF-1.4 report"u8.ToArray()) + "\r\n--m--\r\n";
        DeliveryResult result = await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext { EnvelopeFrom = "lab@lab.test", EnvelopeTo = ResultsAddress, RawBytes = Encoding.UTF8.GetBytes(raw) });
        Assert.Equal(DeliveryOutcome.Accepted, result.Outcome);

        using var unsigned = new AnjalClient(this.baseAddress, this.key);
        AnjalApiException needsSigning = await Assert.ThrowsAsync<AnjalApiException>(() => unsigned.InboxAsync());
        Assert.Equal("signature_required", needsSigning.Code);

        using var client = new AnjalClient(this.baseAddress, this.key, signing, keyId);
        Page<InboxItem> page = await client.InboxAsync();
        InboxItem item = Assert.Single(page.Items);
        Assert.Equal("lab@lab.test", item.From);
        Assert.Null(page.Next);
        InboxMessage envelope = await client.GetInboxMessageAsync(item.Id);
        Assert.Equal(16, envelope.KeyId.Length);
        DecryptedMail mail = envelope.Decrypt(appKey);
        Assert.Equal("Culture — bed 12", mail.Subject);
        Assert.Contains("No growth — 48 h.", mail.Text, StringComparison.Ordinal);
        MailFile file = Assert.Single(mail.Attachments);
        Assert.Equal("report.pdf", file.FileName);
        Assert.Equal("%PDF-1.4 report"u8.ToArray(), file.Content);
        using (RSA wrong = RSA.Create(3072))
        {
            Assert.ThrowsAny<CryptographicException>(() => envelope.Decrypt(wrong));
        }

        await client.AcknowledgeAsync(item.Id);
        Assert.Equal(new[] { item.Id }, await client.AcknowledgeAsync(new[] { item.Id }));
        Assert.Empty((await client.InboxAsync()).Items);
    }

    [Fact]
    public async Task Retries_KeepTheSameIdempotencyKey_AndFollowRetryAfter()
    {
        var seen = new List<string>();
        int calls = 0;
        using var handler = new StubHandler(req =>
        {
            calls++;
            seen.Add(req.Headers.GetValues("Idempotency-Key").Single());
            if (calls == 1)
            {
                var busy = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":\"evidence_unavailable\",\"message\":\"try again\",\"requestId\":\"r1\"}") };
                busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
                return busy;
            }
            if (calls == 2)
            {
                var conflict = new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{\"error\":\"idempotency_in_progress\",\"message\":\"wait\",\"requestId\":\"r2\"}") };
                conflict.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
                return conflict;
            }
            return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("{\"id\":\"" + Guid.Empty + "\",\"status\":\"queued\",\"messageId\":\"x@sgh.in\"}") };
        });
        using var http = new HttpClient(handler);
        using var client = new AnjalClient(new Uri("https://mail.example.test"), this.key, httpClient: http);
        SendResult sent = await client.SendAsync(Message("priya@gmail.test"));
        Assert.Equal("queued", sent.Status);
        Assert.Equal(3, calls);
        Assert.Single(seen.Distinct());

        client.MaxAttempts = 1;
        calls = 0;
        AnjalApiException e = await Assert.ThrowsAsync<AnjalApiException>(() => client.SendAsync(Message("priya@gmail.test")));
        Assert.Equal("evidence_unavailable", e.Code);
    }

    [Fact]
    public void Webhooks_AreVerified_AndTheSigningStringMatchesTheServers()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string body = "{\"type\":\"message.delivered\"}";
        string header = AppWebhookWorker.Signature("whsec_abc", now.ToUnixTimeSeconds(), body);
        Assert.True(WebhookVerifier.Verify("whsec_abc", header, body, now));
        Assert.False(WebhookVerifier.Verify("whsec_abc", header, body + " ", now));
        Assert.False(WebhookVerifier.Verify("whsec_other", header, body, now));
        Assert.False(WebhookVerifier.Verify("whsec_abc", header, body, now.AddMinutes(10)));
        Assert.False(WebhookVerifier.Verify("whsec_abc", "nonsense", body, now));
        byte[] payload = "{\"a\":1}"u8.ToArray();
        Assert.Equal(AppApiService.SigningString("POST", "/api/v1/inbox/ack", "1793000000", payload), AnjalClient.SigningString("post", "/api/v1/inbox/ack", 1793000000, payload));
    }

    private static SendRequest Message(string to)
    {
        var m = new SendRequest { From = new EmailAddress("no-reply@sgh.in", "LiPi HIS"), Subject = "Your result is ready", Text = "Your lab result is ready." };
        m.To.Add(new EmailAddress(to));
        m.Tags.Add("result");
        m.Metadata["userId"] = "u_1";
        return m;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> answer;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) => this.answer = answer;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(this.answer(request));
    }
}
