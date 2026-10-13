using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Server.Tests;

/// <summary>
/// rc.17 (design review [7d], owner 11 Oct 2026): the do-not-send list. An application's mail to an
/// address that bounced hard, or was given up on three times, is not sent again; people's own mail
/// is never held by it.
/// </summary>
public sealed class Rc17DoNotSendTests
{
    private sealed class Recorder : IMailSender
    {
        public List<string> Sent { get; } = new();

        public SendOutcome Next { get; set; } = SendOutcome.Sent;

        public string Reply { get; set; } = "250 2.0.0 OK";

        public Task<SendResult> SendAsync(OutboundDelivery delivery, CancellationToken ct = default)
        {
            this.Sent.Add(delivery.EnvelopeTo[0]);
            return Task.FromResult(new SendResult { Outcome = this.Next, Message = this.Reply, RemoteHost = "mx.example.test" });
        }
    }

    private static async Task<(InMemoryMessageStore Store, System.Guid Tenant)> OrganisationAsync()
    {
        var store = new InMemoryMessageStore();
        TenantRow tenant = await store.UpsertTenantAsync(new TenantRow { Slug = "sgh" });
        await store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "sgh.in" });
        await store.SetTenantDocumentAsync(tenant.Id, DoNotSend.AppsKind, "[{\"Id\":\"his\",\"Name\":\"HIS\",\"SendsAs\":\"appointments@sgh.in\"}]");
        return (store, tenant.Id);
    }

    [Theory]
    [InlineData("550 5.1.1 The email account that you tried to reach does not exist", "Mailbox gone")]
    [InlineData("550 5.1.1 <x@y>: Recipient address rejected: User unknown", "Mailbox gone")]
    [InlineData("556 5.1.10 example.test does not accept mail (null MX, RFC 7505)", "No such domain")]
    [InlineData("550 5.7.1 Message rejected as spam", null)]
    [InlineData("552 5.2.2 Mailbox full", null)]
    public void OnlyAnAddressThatIsGone_IsAHardBounce(string reply, string? reason)
    {
        Assert.Equal(reason, DoNotSend.HardReason(reply));
    }

    [Fact]
    public async Task AHardBounce_OrAThirdGiveUp_PutsTheAddressOnTheList_ForApplicationsOnly()
    {
        (InMemoryMessageStore store, System.Guid tenant) = await OrganisationAsync();
        System.DateTimeOffset now = System.DateTimeOffset.UtcNow;
        Assert.Equal("Mailbox gone", await DoNotSend.NoteFailureAsync(store, "appointments@sgh.in", "gone@gmail.test", "550 5.1.1 no such user", true, now));
        Assert.Equal("Mailbox gone", await DoNotSend.CheckAsync(store, "appointments@sgh.in", "Gone@Gmail.test"));
        // A person's own mail to the same address is never held.
        Assert.Null(await DoNotSend.CheckAsync(store, "doctor@sgh.in", "gone@gmail.test"));
        Assert.Null(await DoNotSend.NoteFailureAsync(store, "doctor@sgh.in", "other@gmail.test", "550 5.1.1 no such user", true, now));

        Assert.Null(await DoNotSend.NoteFailureAsync(store, "appointments@sgh.in", "slow@rediff.test", "Give-up reached", false, now));
        Assert.Null(await DoNotSend.NoteFailureAsync(store, "appointments@sgh.in", "slow@rediff.test", "Give-up reached", false, now));
        Assert.Equal("Gave up 3 times", await DoNotSend.NoteFailureAsync(store, "appointments@sgh.in", "slow@rediff.test", "Give-up reached", false, now));
        DoNotSendDocument doc = await DoNotSend.ReadAsync(store, tenant);
        Assert.Equal(2, doc.Entries.Count);
        Assert.All(doc.Entries, e => Assert.Equal(DoNotSend.ByAnjal, e.By));
    }

    [Fact]
    public async Task TheQueue_MarksAListedMessageSuppressed_AndNeverSendsIt()
    {
        (InMemoryMessageStore store, System.Guid tenant) = await OrganisationAsync();
        await DoNotSend.WriteAsync(store, tenant, new DoNotSendDocument { Entries = { new DoNotSendEntry { Address = "old.patient@hotmail.test", Reason = "Mailbox gone", Added = System.DateTimeOffset.UtcNow, By = "admin@sgh.in" } } });
        var sender = new Recorder();
        var worker = new OutboundWorker(store, sender, new OutboundWorkerOptions
        {
            DoNotSend = (m, ct) => DoNotSend.CheckAsync(store, m.EnvelopeFrom, m.EnvelopeTo, ct),
        }, () => System.DateTimeOffset.UtcNow, log: null);
        System.DateTimeOffset now = System.DateTimeOffset.UtcNow;
        OutboundMessage held = await store.EnqueueOutboundAsync(new OutboundMessage { EnvelopeFrom = "appointments@sgh.in", EnvelopeTo = "old.patient@hotmail.test", RawBytes = "Subject: t\r\n\r\nx\r\n"u8.ToArray(), CreatedAt = now, NextAttemptAt = now, GiveUpAt = now.AddDays(1) });
        OutboundMessage personal = await store.EnqueueOutboundAsync(new OutboundMessage { EnvelopeFrom = "doctor@sgh.in", EnvelopeTo = "old.patient@hotmail.test", RawBytes = "Subject: t\r\n\r\nx\r\n"u8.ToArray(), CreatedAt = now, NextAttemptAt = now, GiveUpAt = now.AddDays(1) });
        await worker.DrainOnceAsync();
        OutboundMessage after = (await store.GetOutboundByIdAsync(held.Id))!;
        Assert.Equal(OutboundStatus.Failed, after.Status);
        Assert.Equal(DoNotSend.SuppressedPrefix + "Mailbox gone", after.LastError);
        Assert.Equal(OutboundStatus.Sent, (await store.GetOutboundByIdAsync(personal.Id))!.Status);
        Assert.Single(sender.Sent);
    }
}
