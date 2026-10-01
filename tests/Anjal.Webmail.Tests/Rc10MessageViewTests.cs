using Anjal.Mailbox;
using Anjal.Mime;
using Anjal.Smtp;
using Anjal.Store;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

/// <summary>
/// v1.0.0-rc.10 (DEF-079): a well-formed message with no text part - a DMARC
/// report from Google is a single zip - says so instead of showing its raw
/// header block; only a message that cannot be parsed is shown raw.
/// </summary>
public sealed class Rc10MessageViewTests : System.IDisposable
{
    private static readonly string[] ArunRecipient = new[] { "arun@anjal.co.in" };

    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-rc10-view-" + System.Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly MaildirStore maildir;
    private readonly MailboxService svc;
    private MailboxRow mailbox = new();

    public Rc10MessageViewTests()
    {
        this.maildir = new MaildirStore(this.root, "test");
        this.svc = new MailboxService(this.store, this.store, this.maildir, "anjal.localhost");
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(this.root))
        {
            System.IO.Directory.Delete(this.root, recursive: true);
        }
    }

    private async Task<MessageView> OpenDeliveredAsync(string raw)
    {
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        this.mailbox = await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.co.in",
            DisplayName = "Arun Shiva",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery"),
        });
        var sink = new MailboxSink(this.store, this.maildir);
        DeliveryResult r = await sink.DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "noreply-dmarc-support@google.com",
            EnvelopeTo = ArunRecipient,
            RawBytes = System.Text.Encoding.UTF8.GetBytes(raw),
        });
        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
        MessageRow row = this.store.MailboxMessages[this.store.MailboxMessages.Count - 1];
        MessageView? view = await this.svc.OpenAsync(this.mailbox.Id, row.Id, allowRemoteImages: false);
        Assert.NotNull(view);
        return view!;
    }

    [Fact]
    public async Task GooglesReport_ASingleZip_SaysSo_AndListsTheAttachment()
    {
        // The shape of Google's DMARC aggregate report: the whole message is one zip.
        string raw =
            "Received: from mail-qk1-f202.google.com\r\n" +
            "From: noreply-dmarc-support@google.com\r\n" +
            "To: arun@anjal.co.in\r\n" +
            "Subject: Report domain: anjal.co.in Submitter: google.com Report-ID: 1\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: application/zip; name=\"google.com!anjal.co.in!1!2.zip\"\r\n" +
            "Content-Disposition: attachment; filename=\"google.com!anjal.co.in!1!2.zip\"\r\n" +
            "Content-Transfer-Encoding: base64\r\n" +
            "\r\n" +
            "UEsDBBQAAAAIAA==\r\n";
        MessageView view = await this.OpenDeliveredAsync(raw);
        Assert.Contains(MailboxService.NoTextNotice, view.BodyHtml, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Received:", view.BodyHtml, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Content-Type", view.BodyHtml, System.StringComparison.Ordinal);
        Assert.Single(view.Attachments);
    }

    [Fact]
    public async Task MultipartWithOnlyAnAttachment_SaysSo()
    {
        string raw =
            "From: a@example.com\r\nTo: arun@anjal.co.in\r\nSubject: only a file\r\nMIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=B\r\n\r\n" +
            "--B\r\nContent-Type: application/pdf\r\nContent-Disposition: attachment; filename=\"a.pdf\"\r\nContent-Transfer-Encoding: base64\r\n\r\nJVBERi0x\r\n" +
            "--B--\r\n";
        MessageView view = await this.OpenDeliveredAsync(raw);
        Assert.Contains(MailboxService.NoTextNotice, view.BodyHtml, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Subject:", view.BodyHtml, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMessageThatCannotBeParsed_IsStillShownRaw()
    {
        // More parts than the parser accepts: it refuses the message - proven here first.
        var sb = new System.Text.StringBuilder("From: a@example.com\r\nTo: arun@anjal.co.in\r\nSubject: too many parts\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=B\r\n\r\n");
        for (int i = 0; i <= MimeParser.MaxParts; i++)
        {
            sb.Append("--B\r\nContent-Type: text/plain\r\n\r\nx\r\n");
        }
        sb.Append("--B--\r\n");
        string raw = sb.ToString();
        Assert.Throws<MimeParseException>(() => MimeParser.Parse(System.Text.Encoding.UTF8.GetBytes(raw)));
        MessageView view = await this.OpenDeliveredAsync(raw);
        Assert.Contains("too many parts", view.BodyHtml, System.StringComparison.Ordinal);
        Assert.DoesNotContain(MailboxService.NoTextNotice, view.BodyHtml, System.StringComparison.Ordinal);
    }
}
