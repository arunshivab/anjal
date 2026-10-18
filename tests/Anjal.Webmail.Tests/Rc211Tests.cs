using System.Net;
using System.Text;
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
/// v1.0.0-rc.21.1 (DEF-117, found 16 Oct 2026): every queued message - applications' mail and
/// people's - was kept whole for ever, although Organisation &gt; Retention says Outbox copies are
/// removed after 7 days. Owner's decision (16 Oct, option (a)): once delivery has ended and the
/// keep-days have passed, the content goes and the header block stays, so Sending systems keeps
/// its record; the untouched original stays in the evidence store.
/// </summary>
public sealed class Rc211Tests : IAsyncLifetime, IDisposable
{
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc211-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private HttpClient client = null!;
    private MailboxService svc = null!;
    private TenantRow tenant = new();
    private MailboxRow arun = new();
    private OutboundMessage delivered = new();
    private OutboundMessage waiting = new();
    private OutboundMessage bounced = new();
    private OutboundMessage other = new();
    private OutboundMessage personal = new();
    private OutboundMessage system = new();

    public void Dispose() => this.client?.Dispose();

    public async Task InitializeAsync()
    {
        this.maildir = new MaildirStore(this.root, "test");
        this.tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "sgh", DisplayName = "Sri Ganesh Hospital", PostmasterMailbox = "arun@sgh.in" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.tenant.Id, Domain = "sgh.in" });
        TenantRow otherOrg = await this.store.UpsertTenantAsync(new TenantRow { Slug = "other", DisplayName = "Other" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = otherOrg.Id, Domain = "other.test" });
        this.arun = await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = this.tenant.Id,
            LocalPart = "arun",
            Domain = "sgh.in",
            DisplayName = "Arun",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        });
        this.app = Program.CreateApp(Array.Empty<string>(), this.store, this.maildir, "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
        this.svc = this.app.Services.GetRequiredService<MailboxService>();
        Assert.Null((await this.svc.AddAppAsync(this.tenant, "HIS", "Lab reports", "reports@sgh.in", string.Empty)).Error);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        this.delivered = await this.QueueAsync("reports@sgh.in", "anita@gmail.test", "Your lab results are ready", now.AddMinutes(-30));
        await this.store.MarkOutboundResultAsync(this.delivered.Id, OutboundStatus.Sent, now, "250 2.0.0 OK");
        this.waiting = await this.QueueAsync("reports@sgh.in", "ramesh@rediffmail.test", "Your appointment is confirmed", now.AddMinutes(-20));
        await this.store.MarkOutboundResultAsync(this.waiting.Id, OutboundStatus.Pending, now.AddMinutes(40), "451 4.7.1 Try again later");
        this.bounced = await this.QueueAsync("reports@sgh.in", "noone@yahoo.test", "Your discharge summary", now.AddMinutes(-10));
        await this.store.MarkOutboundResultAsync(this.bounced.Id, OutboundStatus.Failed, now, "550 5.1.1 No such user");
        this.other = await this.QueueAsync("alerts@other.test", "x@gmail.test", "Another organisation's mail", now.AddMinutes(-5));
        await this.store.MarkOutboundResultAsync(this.other.Id, OutboundStatus.Sent, now, "250 OK");
        this.personal = await this.QueueAsync("arun@sgh.in", "friend@gmail.test", "Dinner on Friday", now.AddMinutes(-5));
        await this.store.MarkOutboundResultAsync(this.personal.Id, OutboundStatus.Sent, now, "250 OK");
        this.system = await this.QueueAsync("postmaster@mail.anjal.test", "someone@gmail.test", "Delivery report", now.AddMinutes(-5));
        await this.store.MarkOutboundResultAsync(this.system.Id, OutboundStatus.Sent, now, "250 OK");

        string address = this.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        this.client = new HttpClient(handler) { BaseAddress = new Uri(address + "/") };
        string html = await (await this.client.GetAsync(new Uri("sign-in", UriKind.Relative))).Content.ReadAsStringAsync();
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", TokenRegex.Match(html).Groups[1].Value), new("address", "arun@sgh.in"), new("password", "correct horse battery") };
        using var content = new FormUrlEncodedContent(form);
        Assert.Equal(HttpStatusCode.Redirect, (await this.client.PostAsync(new Uri("auth/login", UriKind.Relative), content)).StatusCode);
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
    public async Task DeliveredMail_LosesItsContent_AfterTheKeepDays_AndKeepsItsRecord()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.Equal(0, await this.svc.ClearOldOutboxAsync(now));
        Assert.Contains("body", Text(this.delivered), StringComparison.Ordinal);

        // The organisation's default is 7 days.
        Assert.True(await this.svc.ClearOldOutboxAsync(now.AddDays(8)) >= 4);
        foreach (OutboundMessage m in new[] { this.delivered, this.bounced, this.other, this.personal })
        {
            Assert.NotNull(m.BodyRemovedAt);
            Assert.EndsWith("\r\n\r\n", Text(m), StringComparison.Ordinal);
            Assert.DoesNotContain("body", Text(m), StringComparison.Ordinal);
        }
        Assert.Equal("Your lab results are ready", MailboxService.HeaderOf(this.delivered.RawBytes, "Subject"));

        // Mail still being tried is never touched, however old.
        await this.svc.ClearOldOutboxAsync(now.AddDays(60));
        Assert.Null(this.waiting.BodyRemovedAt);
        Assert.Contains("body", Text(this.waiting), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachOrganisation_KeepsItsOwnDays_AndAnjalsOwnMailGoesAfter30()
    {
        RetentionPolicy p = await this.svc.RetentionOfAsync(this.tenant);
        p.OutboxDays = 1;
        Assert.Null(await this.svc.SaveRetentionAsync(this.tenant, p));
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await this.svc.ClearOldOutboxAsync(now.AddDays(2));
        Assert.NotNull(this.delivered.BodyRemovedAt);
        Assert.Null(this.other.BodyRemovedAt);
        Assert.Null(this.system.BodyRemovedAt);

        await this.svc.ClearOldOutboxAsync(now.AddDays(10));
        Assert.NotNull(this.other.BodyRemovedAt);
        Assert.Null(this.system.BodyRemovedAt);

        await this.svc.ClearOldOutboxAsync(now.AddDays(31));
        Assert.NotNull(this.system.BodyRemovedAt);
    }

    [Fact]
    public async Task AMailboxUnderLegalHold_KeepsItsMailWhole()
    {
        Assert.Null(await this.svc.SetHoldAsync(this.tenant, this.arun.Id, true, "Arun", "Court case 12/2026"));
        await this.svc.ClearOldOutboxAsync(DateTimeOffset.UtcNow.AddDays(40));
        Assert.Null(this.personal.BodyRemovedAt);
        Assert.Contains("body", Text(this.personal), StringComparison.Ordinal);
        Assert.NotNull(this.delivered.BodyRemovedAt);
    }

    [Fact]
    public async Task SendingSystems_SaysTheContentWasRemoved_AndOffersNoOriginal()
    {
        Assert.Equal(HttpStatusCode.OK, (await this.client.GetAsync(new Uri("org/sending/" + this.delivered.Id + ".eml", UriKind.Relative))).StatusCode);
        await this.svc.ClearOldOutboxAsync(DateTimeOffset.UtcNow.AddDays(8));

        string detail = WebUtility.HtmlDecode(await this.client.GetStringAsync(new Uri("org/apps?tab=messages&msg=" + this.delivered.Id, UriKind.Relative)));
        Assert.Contains("Your lab results are ready", detail, StringComparison.Ordinal);
        Assert.Contains("after the days set under Retention. This record stays; the untouched original is kept as legal evidence.", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("/org/sending/" + this.delivered.Id + ".eml", detail, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await this.client.GetAsync(new Uri("org/sending/" + this.delivered.Id + ".eml", UriKind.Relative))).StatusCode);

        string list = await this.client.GetStringAsync(new Uri("org/apps?tab=messages", UriKind.Relative));
        Assert.Contains("Your discharge summary", list, StringComparison.Ordinal);
        string retention = WebUtility.HtmlDecode(await this.client.GetStringAsync(new Uri("org/retention", UriKind.Relative)));
        Assert.Contains("Applications' mail too. Your Sent folder is not affected.", retention, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMessageWithBareLineEnds_KeepsItsHeaders_Too()
    {
        OutboundMessage bare = await this.store.EnqueueOutboundAsync(new OutboundMessage
        {
            EnvelopeFrom = "reports@sgh.in",
            EnvelopeTo = "a@b.test",
            RawBytes = Encoding.UTF8.GetBytes("Subject: Bare\nFrom: <reports@sgh.in>\n\nthe content\n"),
        });
        await this.store.MarkOutboundResultAsync(bare.Id, OutboundStatus.Sent, DateTimeOffset.UtcNow, "250 OK");
        await this.svc.ClearOldOutboxAsync(DateTimeOffset.UtcNow.AddDays(8));
        Assert.Equal("Subject: Bare\nFrom: <reports@sgh.in>\n\n", Text(bare));
    }

    private static string Text(OutboundMessage m) => Encoding.UTF8.GetString(m.RawBytes);

    private Task<OutboundMessage> QueueAsync(string from, string to, string subject, DateTimeOffset at) =>
        this.store.EnqueueOutboundAsync(new OutboundMessage
        {
            EnvelopeFrom = from,
            EnvelopeTo = to,
            RawBytes = Encoding.UTF8.GetBytes($"From: <{from}>\r\nTo: <{to}>\r\nSubject: {subject}\r\nMessage-ID: <{Guid.NewGuid():N}@sgh.in>\r\n\r\nbody\r\n"),
            CreatedAt = at,
            NextAttemptAt = at,
            GiveUpAt = at.AddDays(1),
        });
}
