using Anjal.Smtp;
using Anjal.Spam;
using Anjal.Store;

namespace Anjal.Mailbox.Tests;

/// <summary>
/// DEF-065: in production every inbound message passes the spam filter before
/// the mailbox. The filter rebuilt the delivery details and dropped how the
/// message arrived, so encrypted mail (Yahoo, Spamhaus) was shown as having
/// arrived unencrypted. These deliver through the real chain.
/// </summary>
public sealed class Def065EncryptionThroughSpamFilterTests : System.IDisposable
{
    private static readonly byte[] Sample = System.Text.Encoding.ASCII.GetBytes(
        "From: Shiva <arunshiva_b@yahoo.com>\r\n" +
        "To: arun@anjal.co.in\r\n" +
        "Subject: Re: testing firewall\r\n" +
        "Date: Sun, 27 Sep 2026 18:22:40 +0000\r\n" +
        "Message-ID: <y1@yahoo.com>\r\n" +
        "\r\n" +
        "Thanks for the message.\r\n");

    private static readonly string[] Recipient = { "arun@anjal.co.in" };

    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-def065-" + System.Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();

    public void Dispose()
    {
        if (System.IO.Directory.Exists(this.root))
        {
            System.IO.Directory.Delete(this.root, recursive: true);
        }
    }

    private async Task<MessageRow> DeliverThroughTheProductionChainAsync(string? tls)
    {
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "arun", Domain = "anjal.co.in" });
        var mailbox = new MailboxSink(this.store, new MaildirStore(this.root, "test"), _ => { });
        var filter = new SpamFilterSink(new SpamScorer(), mailbox);
        DeliveryResult result = await filter.DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "arunshiva_b@yahoo.com",
            EnvelopeTo = Recipient,
            RawBytes = Sample,
            RemoteAddress = "66.163.185.1",
            ClientHostName = "sonic.mail.yahoo.com",
            TransportTls = tls,
        });
        Assert.Equal(DeliveryOutcome.Accepted, result.Outcome);
        return this.store.MailboxMessages[^1];
    }

    [Fact]
    public async Task EncryptedMail_ThroughTheSpamFilter_IsRecordedAsEncrypted_WithItsCipher()
    {
        MessageRow row = await this.DeliverThroughTheProductionChainAsync("TLSv1.3 TLS_AES_128_GCM_SHA256");
        Assert.True(row.TransportEncrypted);
        Assert.Equal("TLSv1.3 TLS_AES_128_GCM_SHA256", row.TransportTls);
    }

    [Fact]
    public async Task UnencryptedMail_ThroughTheSpamFilter_IsStillRecordedAsUnencrypted()
    {
        MessageRow row = await this.DeliverThroughTheProductionChainAsync(null);
        Assert.False(row.TransportEncrypted);
        Assert.Null(row.TransportTls);
    }
}
