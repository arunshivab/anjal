using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Server.Tests;

/// <summary>DEF-002: authenticated submission must reach outside addresses and file a Sent copy.</summary>
public sealed class SubmissionSinkTests : IDisposable
{
    private static readonly string[] Colleague = { "colleague@qa.test" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-sub-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly MailboxSink mailboxes;
    private readonly SubmissionSink sink;
    private MailboxRow arun = new();
    private MailboxRow colleague = new();

    private sealed class Domains : ILocalDomainResolver
    {
        public Task<bool> IsLocalAsync(string domain, CancellationToken ct = default) =>
            Task.FromResult(string.Equals(domain, "qa.test", StringComparison.OrdinalIgnoreCase));
    }

    public SubmissionSinkTests()
    {
        this.mailboxes = new MailboxSink(this.store, new MaildirStore(this.root, "test"));
        this.sink = new SubmissionSink(this.mailboxes, new Domains(), this.store, this.mailboxes);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            TestFolders.Delete(this.root);
        }
    }

    private async Task SeedAsync()
    {
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "qa" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "qa.test" });
        this.arun = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "arun", Domain = "qa.test" });
        this.colleague = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "colleague", Domain = "qa.test" });
    }

    private static DeliveryContext Submitted(string user, params string[] to) => new()
    {
        EnvelopeFrom = "arun@qa.test",
        EnvelopeTo = to,
        AuthenticatedUser = user,
        RawBytes = System.Text.Encoding.ASCII.GetBytes("From: arun@qa.test\r\nTo: x\r\nSubject: Referral\r\nMessage-ID: <m1@qa.test>\r\n\r\nbody\r\n"),
    };

    private async Task<int> CountInAsync(MailboxRow mailbox, string folder)
    {
        FolderRow? f = (await this.store.ListFoldersAsync(mailbox.Id)).FirstOrDefault(x => x.Name == folder);
        return f is null ? 0 : (await this.store.ListMessagesAsync(mailbox.Id, f.Id, 100, 0)).Count;
    }

    private async Task<IReadOnlyList<OutboundMessage>> QueuedAsync() =>
        await this.store.LeaseOutboundBatchAsync(100, DateTimeOffset.UtcNow.AddMinutes(1));

    [Fact]
    public async Task Rc26_MailToTheOutside_MustComeFromTheOrganisationsOwnAddress_SoTheDailyFigureCountsIt()
    {
        await this.SeedAsync();
        DeliveryContext empty = Submitted("arun@qa.test", "doctor@hospital.example");
        DeliveryResult nullSender = await this.sink.DeliverAsync(new DeliveryContext { EnvelopeFrom = string.Empty, EnvelopeTo = empty.EnvelopeTo, AuthenticatedUser = empty.AuthenticatedUser, RawBytes = empty.RawBytes });
        Assert.Equal(DeliveryOutcome.PermanentFailure, nullSender.Outcome);
        DeliveryResult foreign = await this.sink.DeliverAsync(new DeliveryContext { EnvelopeFrom = "arun@gmail.example", EnvelopeTo = empty.EnvelopeTo, AuthenticatedUser = empty.AuthenticatedUser, RawBytes = empty.RawBytes });
        Assert.Equal(DeliveryOutcome.PermanentFailure, foreign.Outcome);
        Assert.Empty(await this.QueuedAsync());
        // Mail between the organisation's own people is not affected.
        Assert.Equal(DeliveryOutcome.Accepted, (await this.sink.DeliverAsync(new DeliveryContext { EnvelopeFrom = string.Empty, EnvelopeTo = Colleague, AuthenticatedUser = "arun@qa.test", RawBytes = empty.RawBytes })).Outcome);
    }

    [Fact]
    public async Task AnExternalRecipient_IsQueuedForDelivery_AndFiledInSent()
    {
        await this.SeedAsync();
        DeliveryResult r = await this.sink.DeliverAsync(Submitted("arun@qa.test", "doctor@hospital.example"));

        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
        OutboundMessage queued = Assert.Single(await this.QueuedAsync());
        Assert.Equal("doctor@hospital.example", queued.EnvelopeTo);
        Assert.Equal("arun@qa.test", queued.EnvelopeFrom);
        Assert.Equal(1, await this.CountInAsync(this.arun, "Sent"));
        MessageRow copy = (await this.store.ListMessagesAsync(this.arun.Id, (await this.store.ListFoldersAsync(this.arun.Id)).First(f => f.Name == "Sent").Id, 10, 0)).Single();
        Assert.True(copy.Seen);
        Assert.Equal("Referral", copy.Subject);
    }

    [Fact]
    public async Task MixedRecipients_LocalIsDeliveredDirectly_ExternalIsQueued_OneSentCopy()
    {
        await this.SeedAsync();
        DeliveryResult r = await this.sink.DeliverAsync(Submitted("arun@qa.test", "colleague@qa.test", "a@one.example", "b@two.example"));

        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
        Assert.Equal(1, await this.CountInAsync(this.colleague, "INBOX"));
        IReadOnlyList<OutboundMessage> queued = await this.QueuedAsync();
        Assert.Equal(2, queued.Count);
        Assert.DoesNotContain(queued, q => q.EnvelopeTo == "colleague@qa.test");
        Assert.Equal(1, await this.CountInAsync(this.arun, "Sent"));
    }

    [Fact]
    public async Task OnlyAnUnknownLocalRecipient_IsRefused_NothingQueuedOrFiled()
    {
        await this.SeedAsync();
        DeliveryResult r = await this.sink.DeliverAsync(Submitted("arun@qa.test", "nobody@qa.test"));

        Assert.Equal(DeliveryOutcome.PermanentFailure, r.Outcome);
        Assert.Empty(await this.QueuedAsync());
        Assert.Equal(0, await this.CountInAsync(this.arun, "Sent"));
    }

    [Fact]
    public async Task AServiceAccount_SendsOut_ButHasNoSentCopy()
    {
        await this.SeedAsync();
        DeliveryResult r = await this.sink.DeliverAsync(Submitted("sigma", "patient@example.org"));

        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
        Assert.Single(await this.QueuedAsync());
        Assert.Equal(0, await this.CountInAsync(this.arun, "Sent"));
    }

    [Fact]
    public async Task WithoutALogin_NothingIsSentOutside()
    {
        await this.SeedAsync();
        var anonymous = new DeliveryContext
        {
            EnvelopeFrom = "x@elsewhere.test",
            EnvelopeTo = new[] { "doctor@hospital.example" },
            RawBytes = System.Text.Encoding.ASCII.GetBytes("Subject: x\r\n\r\nbody\r\n"),
        };
        await this.sink.DeliverAsync(anonymous);
        Assert.Empty(await this.QueuedAsync());
    }

    [Fact]
    public async Task AnApplicationsMail_GetsAListUnsubscribeHeader_UnlessItWroteItsOwn_AndAPersonsMailDoesNot()
    {
        // rc.22 (item 51), tested in rc.22.2: mail an application sends offers a way out.
        await this.SeedAsync();
        Assert.Equal(DeliveryOutcome.Accepted, (await this.sink.DeliverAsync(Submitted("app-lipi", "patient@gmail.example"))).Outcome);
        OutboundMessage app = Assert.Single(await this.QueuedAsync());
        string raw = System.Text.Encoding.ASCII.GetString(app.RawBytes);
        Assert.Contains("List-Unsubscribe: <mailto:unsubscribe@qa.test?subject=unsubscribe>\r\n", raw, StringComparison.Ordinal);
        Assert.Equal(1, raw.Split("List-Unsubscribe:").Length - 1);

        DeliveryContext own = Submitted("app-lipi", "patient@gmail.example");
        own = own.WithRawBytes(System.Text.Encoding.ASCII.GetBytes("List-Unsubscribe: <https://lipi.example/leave>\r\n" + System.Text.Encoding.ASCII.GetString(own.RawBytes)));
        Assert.Equal(DeliveryOutcome.Accepted, (await this.sink.DeliverAsync(own)).Outcome);
        OutboundMessage kept = Assert.Single(await this.QueuedAsync());
        string kept2 = System.Text.Encoding.ASCII.GetString(kept.RawBytes);
        Assert.Contains("List-Unsubscribe: <https://lipi.example/leave>", kept2, StringComparison.Ordinal);
        Assert.DoesNotContain("unsubscribe@qa.test", kept2, StringComparison.Ordinal);

        Assert.Equal(DeliveryOutcome.Accepted, (await this.sink.DeliverAsync(Submitted("arun@qa.test", "doctor@hospital.example"))).Outcome);
        OutboundMessage person = Assert.Single(await this.QueuedAsync());
        Assert.DoesNotContain("List-Unsubscribe", System.Text.Encoding.ASCII.GetString(person.RawBytes), StringComparison.Ordinal);
    }
}
