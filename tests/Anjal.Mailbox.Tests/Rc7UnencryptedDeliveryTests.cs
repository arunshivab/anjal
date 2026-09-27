using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Mailbox.Tests;

/// <summary>
/// v1.0.0-rc.7: how mail reached the server is recorded, and a tenant may
/// file unencrypted mail in its own folder unless the sender is trusted.
/// </summary>
public sealed class Rc7UnencryptedDeliveryTests : System.IDisposable
{
    private static readonly byte[] Sample = System.Text.Encoding.ASCII.GetBytes(
        "From: Doctor <doctor@rediffmail.com>\r\n" +
        "To: arun@anjal.co.in\r\n" +
        "Subject: Report\r\n" +
        "Date: Sun, 27 Sep 2026 10:00:00 +0530\r\n" +
        "Message-ID: <r1@rediffmail.com>\r\n" +
        "\r\n" +
        "Body.\r\n");

    private static readonly string[] Recipient = { "arun@anjal.co.in" };

    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-rc7-" + System.Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();

    public void Dispose()
    {
        if (System.IO.Directory.Exists(this.root))
        {
            System.IO.Directory.Delete(this.root, recursive: true);
        }
    }

    private async Task<MailboxRow> SeedAsync(string? unencryptedFolder)
    {
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", UnencryptedFolder = unencryptedFolder });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        return await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "arun", Domain = "anjal.co.in" });
    }

    private async Task<(MessageRow Row, string Folder)> DeliverAsync(string? tls)
    {
        var sink = new MailboxSink(this.store, new MaildirStore(this.root, "test"), _ => { });
        await sink.DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "doctor@rediffmail.com",
            EnvelopeTo = Recipient,
            RawBytes = Sample,
            TransportTls = tls,
        });
        MessageRow row = this.store.MailboxMessages[^1];
        FolderRow folder = (await this.store.ListFoldersAsync(row.MailboxId)).Single(f => f.Id == row.FolderId);
        return (row, folder.Name);
    }

    [Fact]
    public async Task Encrypted_IsRecordedWithItsCipher_AndGoesToInbox()
    {
        await this.SeedAsync("Unencrypted");
        (MessageRow row, string folder) = await this.DeliverAsync("TLSv1.2 TLS_DHE_RSA_WITH_AES_256_GCM_SHA384");
        Assert.True(row.TransportEncrypted);
        Assert.Equal("TLSv1.2 TLS_DHE_RSA_WITH_AES_256_GCM_SHA384", row.TransportTls);
        Assert.Equal(FolderRow.Inbox, folder);
    }

    [Fact]
    public async Task Unencrypted_StaysInInbox_ByDefault()
    {
        await this.SeedAsync(null);
        (MessageRow row, string folder) = await this.DeliverAsync(null);
        Assert.False(row.TransportEncrypted);
        Assert.Null(row.TransportTls);
        Assert.Equal(FolderRow.Inbox, folder);
    }

    [Fact]
    public async Task Unencrypted_GoesToTheTenantsFolder_UnlessTheSenderIsTrusted()
    {
        MailboxRow mailbox = await this.SeedAsync("Unencrypted");
        (_, string first) = await this.DeliverAsync(null);
        Assert.Equal("Unencrypted", first);

        await this.store.AddTrustedSenderAsync(mailbox.Id, "doctor@rediffmail.com");
        (_, string second) = await this.DeliverAsync(null);
        Assert.Equal(FolderRow.Inbox, second);
    }
}
