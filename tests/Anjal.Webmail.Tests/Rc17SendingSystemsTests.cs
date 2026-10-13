using System.Net;
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
/// rc.17 (design review section 10, owner 11 Oct 2026): Sending systems - the overview, every
/// message with its attempts, the actions on it, webhooks with Replay, the do-not-send list, and the
/// Anjal console's Transactional page. Only the organisation's own applications' mail is shown.
/// </summary>
[Collection("Operators")]
public sealed class Rc17SendingSystemsTests : IAsyncLifetime, IDisposable
{
    private static readonly string[] Ramesh = { "ramesh.k@rediffmail.test" };
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc17-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private HttpClient client = null!;
    private TenantRow tenant = new();
    private OutboundMessage delivered = new();
    private OutboundMessage delayed = new();
    private OutboundMessage bounced = new();
    private OutboundMessage foreign = new();

    public void Dispose() => this.client?.Dispose();

    public async Task InitializeAsync()
    {
        this.maildir = new MaildirStore(this.root, "test");
        this.tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "sgh", DisplayName = "Sri Ganesh Hospital", PostmasterMailbox = "arun@sgh.in" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.tenant.Id, Domain = "sgh.in" });
        TenantRow other = await this.store.UpsertTenantAsync(new TenantRow { Slug = "other", DisplayName = "Other" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = other.Id, Domain = "other.test" });
        await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = this.tenant.Id,
            LocalPart = "arun",
            Domain = "sgh.in",
            DisplayName = "Arun",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        });
        this.app = Program.CreateApp(Array.Empty<string>(), this.store, this.maildir, "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        Assert.Null((await svc.AddAppAsync(this.tenant, "HIS", "Appointment confirmations", "appointments@sgh.in", string.Empty)).Error);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        this.delivered = await this.QueueAsync("appointments@sgh.in", "anita@gmail.test", "Your lab results are ready", now.AddMinutes(-30));
        await this.store.MarkOutboundResultAsync(this.delivered.Id, OutboundStatus.Sent, now.AddMinutes(-30).AddSeconds(4), "250 2.0.0 OK");
        this.delayed = await this.QueueAsync("appointments@sgh.in", "ramesh.k@rediffmail.test", "Your appointment on 14 Oct is confirmed", now.AddMinutes(-20));
        await this.store.MarkOutboundResultAsync(this.delayed.Id, OutboundStatus.Pending, now.AddMinutes(40), "451 4.7.1 Please try again later");
        this.bounced = await this.QueueAsync("appointments@sgh.in", "v.sharma@yahoo.test", "Your appointment on 15 Oct is confirmed", now.AddMinutes(-10));
        await this.store.MarkOutboundResultAsync(this.bounced.Id, OutboundStatus.Failed, now.AddMinutes(-10), "550 5.1.1 The email account that you tried to reach does not exist");
        this.foreign = await this.QueueAsync("someone@other.test", "x@gmail.test", "Not theirs", now.AddMinutes(-5));

        // The delayed message's attempt, as the queue records it beside the original kept.
        var evidenceId = Guid.NewGuid();
        await this.store.InsertEvidenceAsync(new EvidenceRow { Id = evidenceId, Direction = "out", CapturedAt = now.AddMinutes(-20), EnvelopeFrom = "appointments@sgh.in", EnvelopeTo = Ramesh, Sha256 = new string('a', 64), Path = "x", Outcome = "pending" });
        await this.store.SetOutboundEvidenceAsync(this.delayed.Id, evidenceId);
        await this.store.AddEvidenceAttemptAsync(new EvidenceAttemptRow { EvidenceId = evidenceId, AttemptedAt = now.AddMinutes(-20), Recipient = "ramesh.k@rediffmail.test", RemoteHost = "mx.rediffmail.test", TransportTls = "TLS 1.2 ECDHE-RSA-AES256-GCM", ReplyCode = 451, ReplyText = "451 4.7.1 Please try again later", Outcome = "deferred" });

        string address = this.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        this.client = new HttpClient(handler) { BaseAddress = new Uri(address + "/") };
        string token = await this.TokenAsync("sign-in");
        HttpResponseMessage res = await this.PostAsync("auth/login", token, ("address", "arun@sgh.in"), ("password", "correct horse battery"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
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
    public void AMessagesStatus_ReadsFromTheQueue()
    {
        Assert.Equal("Delivered", MailboxService.SendingStatus(new OutboundMessage { Status = OutboundStatus.Sent }));
        Assert.Equal("Queued", MailboxService.SendingStatus(new OutboundMessage { Status = OutboundStatus.Pending }));
        Assert.Equal("Delayed", MailboxService.SendingStatus(new OutboundMessage { Status = OutboundStatus.Pending, Attempts = 2 }));
        Assert.Equal("Bounced", MailboxService.SendingStatus(new OutboundMessage { Status = OutboundStatus.Failed, LastError = "550 5.1.1 no such user" }));
        Assert.Equal("Failed", MailboxService.SendingStatus(new OutboundMessage { Status = OutboundStatus.Failed, LastError = "Give-up reached after 7 attempts. Last error: 421" }));
        Assert.Equal("Failed", MailboxService.SendingStatus(new OutboundMessage { Status = OutboundStatus.Failed, LastError = OutboundMessage.CancelledBySender }));
        Assert.Equal("Suppressed", MailboxService.SendingStatus(new OutboundMessage { Status = OutboundStatus.Failed, LastError = DoNotSend.SuppressedPrefix + "Mailbox gone" }));
        Assert.Equal("Your report", MailboxService.HeaderOf("From: a@b\r\nSubject: =?utf-8?Q?Your_report?=\r\n\r\nbody"u8.ToArray(), "Subject"));
    }

    [Fact]
    public async Task TheOverview_CountsOnlyTheOrganisationsApplications()
    {
        string page = await this.client.GetStringAsync(new Uri("org/apps", UriKind.Relative));
        Assert.Contains("<h2>Sending systems</h2>", page, StringComparison.Ordinal);
        foreach (string tab in new[] { "messages", "applications", "webhooks", "donotsend", "dns" })
        {
            Assert.Contains($"href=\"/org/apps?tab={tab}\"", page, StringComparison.Ordinal);
        }
        Assert.Matches("<span>Accepted</span><b>3</b>", page);
        Assert.Matches("<span>Delivered</span><b>1</b>", page);
        Assert.Matches("<span>Bounced</span><b>1</b>", page);
        Assert.DoesNotContain("Not theirs", page, StringComparison.Ordinal);
        string messages = await this.client.GetStringAsync(new Uri("org/apps?tab=messages", UriKind.Relative));
        Assert.Contains("Your appointment on 14 Oct is confirmed", messages, StringComparison.Ordinal);
        Assert.Contains("class=\"sspill ss-bounced\"", messages, StringComparison.Ordinal);
        Assert.DoesNotContain("Not theirs", messages, StringComparison.Ordinal);
        string onlyDelayed = await this.client.GetStringAsync(new Uri("org/apps?tab=messages&status=Delayed", UriKind.Relative));
        Assert.Contains("ramesh.k@rediffmail.test", onlyDelayed, StringComparison.Ordinal);
        Assert.DoesNotContain("anita@gmail.test", onlyDelayed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMessage_ShowsEveryAttempt_AndCanBeTriedOrStopped_OnlyIfItIsTheirs()
    {
        string detail = await this.client.GetStringAsync(new Uri("org/apps?tab=messages&msg=" + this.delayed.Id, UriKind.Relative));
        Assert.Contains("class=\"sstimeline\"", detail, StringComparison.Ordinal);
        Assert.Matches("mx\\.rediffmail\\.test (·|&#xB7;) TLS 1\\.2 ECDHE-RSA-AES256-GCM", detail);
        Assert.Contains("<code>451 4.7.1 Please try again later</code>", detail, StringComparison.Ordinal);
        Assert.Contains("SHA-256 aaaaaaaaaaaa", detail, StringComparison.Ordinal);
        Assert.Contains("action=\"/org/sending/retry\"", detail, StringComparison.Ordinal);

        HttpResponseMessage eml = await this.client.GetAsync(new Uri("org/sending/" + this.delayed.Id + ".eml", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, eml.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await this.client.GetAsync(new Uri("org/sending/" + this.foreign.Id + ".eml", UriKind.Relative))).StatusCode);

        string token = await this.TokenAsync("org/apps?tab=messages&msg=" + this.delayed.Id);
        HttpResponseMessage stopped = await this.PostAsync("org/sending/stop", token, ("id", this.delayed.Id.ToString()), ("back", "/org/apps?tab=messages"));
        Assert.Contains("saved=stopped", stopped.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal(OutboundStatus.Failed, (await this.store.GetOutboundByIdAsync(this.delayed.Id))!.Status);
        // Another organisation's message is not theirs to touch.
        HttpResponseMessage refused = await this.PostAsync("org/sending/retry", token, ("id", this.foreign.Id.ToString()));
        Assert.Contains("e=", refused.Headers.Location!.ToString(), StringComparison.Ordinal);

        string csv = await this.client.GetStringAsync(new Uri("org/sending/messages.csv", UriKind.Relative));
        Assert.StartsWith("time,status,to,subject,application,tries,last reply", csv, StringComparison.Ordinal);
        Assert.Contains("v.sharma@yahoo.test", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("Not theirs", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATest_GoesThroughTheQueue_ToTheAdministrator()
    {
        string apps = await this.client.GetStringAsync(new Uri("org/apps?tab=applications", UriKind.Relative));
        Assert.Contains("smtp://", apps, StringComparison.Ordinal);
        string appId = (await this.app.Services.GetRequiredService<MailboxService>().AppsOfAsync(this.tenant.Id))[0].Id;
        string token = await this.TokenAsync("org/apps?tab=applications");
        HttpResponseMessage sent = await this.PostAsync("org/sending/test", token, ("id", appId));
        Assert.Contains("saved=tested", sent.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Contains(this.store.Outbound, m => m.EnvelopeFrom == "appointments@sgh.in" && m.EnvelopeTo == "arun@sgh.in");
    }

    [Fact]
    public async Task TheDoNotSendList_CanBeAddedToAndTakenOff()
    {
        string token = await this.TokenAsync("org/apps?tab=donotsend");
        HttpResponseMessage bad = await this.PostAsync("org/donotsend", token, ("address", "not an address"));
        Assert.Contains("e=", bad.Headers.Location!.ToString(), StringComparison.Ordinal);
        HttpResponseMessage added = await this.PostAsync("org/donotsend", token, ("address", "K.Bose@gmail.test"), ("reason", "Marked as spam"));
        Assert.Contains("saved=added", added.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal("Marked as spam", await DoNotSend.CheckAsync(this.store, "appointments@sgh.in", "k.bose@gmail.test"));
        string list = await this.client.GetStringAsync(new Uri("org/apps?tab=donotsend", UriKind.Relative));
        Assert.Contains("k.bose@gmail.test", list, StringComparison.Ordinal);
        await this.PostAsync("org/donotsend", token, ("address", "k.bose@gmail.test"), ("remove", "1"));
        Assert.Null(await DoNotSend.CheckAsync(this.store, "appointments@sgh.in", "k.bose@gmail.test"));
    }

    [Fact]
    public async Task AFailedWebhookDelivery_CanBeReplayed()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        InboundMessage inbound = await this.store.SaveInboundMessageAsync(new InboundMessage { RawBytes = "Subject: r\r\n\r\nx"u8.ToArray(), ReceivedAt = now });
        WebhookJob job = await this.store.EnqueueWebhookJobAsync(new WebhookJob { InboundMessageId = inbound.Id, Recipient = "results+p20@sgh.in", LocalPart = "results", Tag = "p20", NextAttemptAt = now, GiveUpAt = now.AddHours(24), CreatedAt = now });
        await this.store.CompleteWebhookJobAsync(job.Id, WebhookJobStatus.Failed, now, "HTTP 503");
        await this.store.SaveWebhookDeliveryAsync(new WebhookDelivery { InboundMessageId = inbound.Id, Url = "https://his.sgh.in/hooks/anjal", StatusCode = 503, AttemptedAt = now });

        string page = await this.client.GetStringAsync(new Uri("org/apps?tab=webhooks", UriKind.Relative));
        Assert.Matches("results(\\+|&#x2B;)p20@sgh\\.in (·|&#xB7;) p20", page);
        Assert.Contains(">503<", page, StringComparison.Ordinal);
        Assert.Contains("<b class=\"ssbadge\">1</b>", page, StringComparison.Ordinal);
        string token = await this.TokenAsync("org/apps?tab=webhooks");
        HttpResponseMessage replayed = await this.PostAsync("org/sending/replay", token, ("id", job.Id.ToString()));
        Assert.Contains("saved=replayed", replayed.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Single(await this.store.LeaseWebhookJobsAsync(10, DateTimeOffset.UtcNow.AddSeconds(1)));
    }

    [Fact]
    public async Task TheConsole_HasATransactionalPage_ForOperators()
    {
        string? before = Environment.GetEnvironmentVariable("ANJAL_OPERATORS");
        try
        {
            Environment.SetEnvironmentVariable("ANJAL_OPERATORS", "arun@sgh.in");
            // A message still being tried, made a moment ago (so it is "today" whatever the hour).
            OutboundMessage slow = await this.QueueAsync("appointments@sgh.in", "p.nair@slow-receiver.test", "Your lab results are ready", DateTimeOffset.UtcNow.AddSeconds(-1));
            await this.store.MarkOutboundResultAsync(slow.Id, OutboundStatus.Pending, DateTimeOffset.UtcNow.AddMinutes(5), "421 4.4.2 Connection timed out");
            string page = await this.client.GetStringAsync(new Uri("ops/transactional", UriKind.Relative));
            Assert.Contains("<h2>Transactional</h2>", page, StringComparison.Ordinal);
            Assert.Contains("Queue right now", page, StringComparison.Ordinal);
            Assert.Contains("Receiving servers that slow us down", page, StringComparison.Ordinal);
            Assert.Contains("slow-receiver.test", page, StringComparison.Ordinal);
            Assert.Contains("421 4.4.2 Connection timed out", page, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANJAL_OPERATORS", before);
        }
    }

    private Task<OutboundMessage> QueueAsync(string from, string to, string subject, DateTimeOffset at) =>
        this.store.EnqueueOutboundAsync(new OutboundMessage
        {
            EnvelopeFrom = from,
            EnvelopeTo = to,
            RawBytes = System.Text.Encoding.UTF8.GetBytes($"From: <{from}>\r\nTo: <{to}>\r\nSubject: {subject}\r\nMessage-ID: <{Guid.NewGuid():N}@sgh.in>\r\n\r\nbody\r\n"),
            CreatedAt = at,
            NextAttemptAt = at,
            GiveUpAt = at.AddDays(1),
        });

    private async Task<string> TokenAsync(string path)
    {
        string html = await (await this.client.GetAsync(new Uri(path, UriKind.Relative))).Content.ReadAsStringAsync();
        Match m = TokenRegex.Match(html);
        Assert.True(m.Success, "no antiforgery token on " + path);
        return m.Groups[1].Value;
    }

    private async Task<HttpResponseMessage> PostAsync(string path, string token, params (string Key, string Value)[] fields)
    {
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        foreach ((string k, string v) in fields)
        {
            form.Add(new KeyValuePair<string, string>(k, v));
        }
        using var content = new FormUrlEncodedContent(form);
        return await this.client.PostAsync(new Uri(path, UriKind.Relative), content);
    }
}
