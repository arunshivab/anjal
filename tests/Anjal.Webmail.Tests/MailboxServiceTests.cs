using Anjal.Mailbox;
using Anjal.Mime;
using Anjal.Smtp;
using Anjal.Store;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

public sealed class MailboxServiceTests : System.IDisposable
{
    private static readonly string[] ArunRecipient = new[] { "arun@anjal.co.in" };

    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-webmail-" + System.Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly MaildirStore maildir;
    private readonly MailboxService svc;
    private TenantRow tenant = new();
    private MailboxRow mailbox = new();

    public MailboxServiceTests()
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

    private async System.Threading.Tasks.Task SeedAsync()
    {
        this.tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa" }).ConfigureAwait(false);
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.tenant.Id, Domain = "anjal.co.in" }).ConfigureAwait(false);
        this.mailbox = await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = this.tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.co.in",
            DisplayName = "Arun Shiva",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery"),
        }).ConfigureAwait(false);
    }

    private async System.Threading.Tasks.Task<MessageRow> DeliverAsync(string raw)
    {
        var sink = new MailboxSink(this.store, this.maildir);
        DeliveryResult r = await sink.DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "sender@example.com",
            EnvelopeTo = ArunRecipient,
            RawBytes = System.Text.Encoding.UTF8.GetBytes(raw),
        }).ConfigureAwait(false);
        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
        return this.store.MailboxMessages[this.store.MailboxMessages.Count - 1];
    }

    private const string HtmlMail =
        "From: =?utf-8?B?U2VuZMOpcg==?= <sender@example.com>\r\n" +
        "To: arun@anjal.co.in\r\n" +
        "Cc: other@example.com\r\n" +
        "Subject: =?utf-8?B?SGVsbG8gw6k=?=\r\n" +
        "Date: Thu, 17 Sep 2026 10:00:00 +0530\r\n" +
        "Message-ID: <h1@example.com>\r\n" +
        "MIME-Version: 1.0\r\n" +
        "Content-Type: multipart/mixed; boundary=OUTER\r\n" +
        "\r\n" +
        "--OUTER\r\n" +
        "Content-Type: multipart/alternative; boundary=INNER\r\n" +
        "\r\n" +
        "--INNER\r\n" +
        "Content-Type: text/plain; charset=utf-8\r\n" +
        "\r\n" +
        "plain version\r\n" +
        "--INNER\r\n" +
        "Content-Type: text/html; charset=utf-8\r\n" +
        "\r\n" +
        "<p>html <b>version</b></p><script>x()</script><img src=\"https://t.example/p.gif\">\r\n" +
        "--INNER--\r\n" +
        "--OUTER\r\n" +
        "Content-Type: application/pdf; name=\"report.pdf\"\r\n" +
        "Content-Disposition: attachment; filename=\"report.pdf\"\r\n" +
        "Content-Transfer-Encoding: base64\r\n" +
        "\r\n" +
        "JVBERi0xLjQK\r\n" +
        "--OUTER--\r\n";

    [Fact]
    public async System.Threading.Tasks.Task ListFolders_CreatesDefaultsWithCounts()
    {
        await this.SeedAsync();
        await this.DeliverAsync("Subject: a\r\n\r\nb\r\n");
        var folders = await this.svc.ListFoldersAsync(this.mailbox.Id);

        Assert.Equal(5, folders.Count);
        Assert.Contains(folders, f => f.Name == "Junk");
        Assert.Equal("INBOX", folders[0].Name);
        Assert.Equal(1, folders[0].Count);
        Assert.Contains(folders, f => f.Name == "Sent" && f.Count == 0);
    }

    [Fact]
    public async System.Threading.Tasks.Task Open_PicksHtmlSanitisesListsAttachmentsAndMarksSeen()
    {
        await this.SeedAsync();
        MessageRow row = await this.DeliverAsync(HtmlMail);
        Assert.False(row.Seen);

        MessageView? view = await this.svc.OpenAsync(this.mailbox.Id, row.Id, allowRemoteImages: false);
        Assert.NotNull(view);
        Assert.True(view!.IsHtml);
        Assert.Contains("<b>version</b>", view.BodyHtml, System.StringComparison.Ordinal);
        Assert.DoesNotContain("<script", view.BodyHtml, System.StringComparison.Ordinal);
        Assert.True(view.HasBlockedImages);
        Assert.Equal("Hello é", view.Subject);
        Assert.Equal("Sendér <sender@example.com>", view.From);
        Assert.Equal("other@example.com", view.Cc);
        AttachmentView a = Assert.Single(view.Attachments);
        Assert.Equal("report.pdf", a.FileName);
        Assert.Equal("application/pdf", a.ContentType);
        Assert.True(view.Row.Seen);

        MessageRow? after = await this.store.GetMessageByIdAsync(row.Id);
        Assert.True(after!.Seen);
        Assert.StartsWith("cur/", after.MaildirFile, System.StringComparison.Ordinal);

        (AttachmentView View, byte[] Bytes)? att = await this.svc.GetAttachmentAsync(this.mailbox.Id, row.Id, 0);
        Assert.NotNull(att);
        Assert.Equal("%PDF-1.4\n", System.Text.Encoding.ASCII.GetString(att!.Value.Bytes));
        Assert.Null(await this.svc.GetAttachmentAsync(this.mailbox.Id, row.Id, 5));
    }

    [Fact]
    public async System.Threading.Tasks.Task Open_PlainTextMail_WrappedAsPre()
    {
        await this.SeedAsync();
        MessageRow row = await this.DeliverAsync("Subject: plain\r\nContent-Type: text/plain\r\n\r\n1 < 2\r\n");
        MessageView? view = await this.svc.OpenAsync(this.mailbox.Id, row.Id, false);
        Assert.False(view!.IsHtml);
        Assert.Contains("1 &lt; 2", view.BodyHtml, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task Open_OtherMailboxesMessage_IsRefused()
    {
        await this.SeedAsync();
        MessageRow row = await this.DeliverAsync("Subject: a\r\n\r\nb\r\n");
        MailboxRow other = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = this.tenant.Id, LocalPart = "other", Domain = "anjal.co.in" });

        Assert.Null(await this.svc.OpenAsync(other.Id, row.Id, false));
        Assert.Null(await this.svc.SetFlagsAsync(other.Id, row.Id, true, true, false));
        Assert.Null(await this.svc.MoveAsync(other.Id, row.Id, "Trash"));
        Assert.False(await this.svc.DeleteAsync(other.Id, row.Id));
        Assert.NotNull(await this.store.GetMessageByIdAsync(row.Id));
    }

    [Fact]
    public async System.Threading.Tasks.Task Flags_Move_Delete_RoundTrip()
    {
        await this.SeedAsync();
        MessageRow row = await this.DeliverAsync("Subject: a\r\n\r\nb\r\n");

        MessageRow? flagged = await this.svc.SetFlagsAsync(this.mailbox.Id, row.Id, seen: true, flagged: true, answered: false);
        Assert.True(flagged!.Flagged);
        Assert.EndsWith("2,FS", flagged.MaildirFile, System.StringComparison.Ordinal);
        Assert.NotNull(await this.maildir.ReadAsync(this.tenant.Slug, this.mailbox.Address, FolderRow.Inbox, flagged.MaildirFile));

        MessageRow? moved = await this.svc.MoveAsync(this.mailbox.Id, row.Id, "Trash");
        FolderRow? trash = await this.svc.GetFolderAsync(this.mailbox.Id, "Trash");
        Assert.Equal(trash!.Id, moved!.FolderId);
        Assert.Null(await this.maildir.ReadAsync(this.tenant.Slug, this.mailbox.Address, FolderRow.Inbox, flagged.MaildirFile));
        Assert.NotNull(await this.maildir.ReadAsync(this.tenant.Slug, this.mailbox.Address, "Trash", moved.MaildirFile));

        long usedBefore = (await this.store.GetMailboxByIdAsync(this.mailbox.Id))!.UsedBytes;
        Assert.True(await this.svc.DeleteAsync(this.mailbox.Id, row.Id));
        Assert.Null(await this.maildir.ReadAsync(this.tenant.Slug, this.mailbox.Address, "Trash", moved.MaildirFile));
        Assert.Equal(usedBefore - row.SizeBytes, (await this.store.GetMailboxByIdAsync(this.mailbox.Id))!.UsedBytes);
        Assert.Null(await this.store.GetMessageByIdAsync(row.Id));
    }

    [Fact]
    public async System.Threading.Tasks.Task ReportSpam_MovesToJunkAndBlocksSender_NotSpam_RestoresAndAllows()
    {
        await this.SeedAsync();
        MessageRow row = await this.DeliverAsync("From: Spam Co <news@spammer.test>\r\nSubject: buy\r\n\r\nbody\r\n");

        MessageRow? junked = await this.svc.ReportSpamAsync(this.mailbox.Id, row.Id);
        FolderRow? junk = await this.svc.GetFolderAsync(this.mailbox.Id, MailboxSink.JunkFolder);
        Assert.Equal(junk!.Id, junked!.FolderId);
        var rules = await this.store.ListSenderRulesAsync(this.tenant.Id);
        SenderRuleRow block = Assert.Single(rules);
        Assert.Equal("news@spammer.test", block.Pattern);
        Assert.Equal(SenderRuleAction.Block, block.Action);

        MessageRow? restored = await this.svc.MarkNotSpamAsync(this.mailbox.Id, row.Id);
        FolderRow? inbox = await this.svc.GetFolderAsync(this.mailbox.Id, FolderRow.Inbox);
        Assert.Equal(inbox!.Id, restored!.FolderId);
        SenderRuleRow allow = Assert.Single(await this.store.ListSenderRulesAsync(this.tenant.Id));
        Assert.Equal(SenderRuleAction.Allow, allow.Action);

        Assert.Null(await this.svc.ReportSpamAsync(System.Guid.NewGuid(), row.Id));
    }

    [Fact]
    public void SenderOf_PrefersFromHeader()
    {
        Assert.Equal("a@b.test", MailboxService.SenderOf(new MessageRow { FromHeader = "A <A@B.test>", EnvelopeFrom = "bounce@x.test" }));
        Assert.Equal("bounce@x.test", MailboxService.SenderOf(new MessageRow { FromHeader = string.Empty, EnvelopeFrom = "Bounce@x.test" }));
    }

    [Fact]
    public async System.Threading.Tasks.Task Send_EnqueuesPerRecipientAndFilesSentCopy()
    {
        await this.SeedAsync();
        var req = new ComposeRequest
        {
            To = "Alice <alice@example.com>, bob@example.com",
            Cc = "carol@example.com",
            Subject = "Réunion demain",
            Body = "Line 1\nLine 2 with ünïcode",
        };
        req.Attachments.Add(("notes.txt", "text/plain", System.Text.Encoding.ASCII.GetBytes("hello")));

        string? error = await this.svc.SendAsync(this.mailbox.Id, req);
        Assert.Null(error);

        var leased = await this.store.LeaseOutboundBatchAsync(10, System.DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(3, leased.Count);
        Assert.All(leased, m => Assert.Equal("arun@anjal.co.in", m.EnvelopeFrom));
        Assert.Contains(leased, m => m.EnvelopeTo == "alice@example.com");
        Assert.Contains(leased, m => m.EnvelopeTo == "carol@example.com");

        MimeMessage parsed = MimeParser.Parse(leased[0].RawBytes);
        Assert.Equal("Réunion demain", parsed.Subject);
        Assert.Equal("\"Arun Shiva\" <arun@anjal.co.in>", parsed.Headers.Get("From"));
        Assert.Contains("alice@example.com", parsed.Headers.Get("To"), System.StringComparison.Ordinal);
        Assert.Equal("carol@example.com", parsed.Headers.Get("Cc"));
        Assert.StartsWith("<", parsed.Headers.Get("Message-ID"), System.StringComparison.Ordinal);
        Assert.EndsWith("@anjal.co.in>", parsed.Headers.Get("Message-ID"), System.StringComparison.Ordinal);
        var mixed = Assert.IsType<MimeMultipart>(parsed.Body);
        Assert.Equal(2, mixed.Parts.Count);
        var text = Assert.IsType<MimePart>(mixed.Parts[0]);
        Assert.Contains("ünïcode", text.GetBodyAsText(), System.StringComparison.Ordinal);
        var att = Assert.IsType<MimePart>(mixed.Parts[1]);
        Assert.Equal("hello", System.Text.Encoding.ASCII.GetString(att.Body));

        FolderRow? sent = await this.svc.GetFolderAsync(this.mailbox.Id, "Sent");
        var (items, total) = await this.svc.ListMessagesAsync(this.mailbox.Id, sent!.Id, 0, 10);
        Assert.Equal(1, total);
        Assert.True(items[0].Seen);
        Assert.Equal("Réunion demain", items[0].Subject);
        MessageView? view = await this.svc.OpenAsync(this.mailbox.Id, items[0].Id, false);
        Assert.Contains("Line 2 with ünïcode", view!.BodyHtml, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task Send_Validation()
    {
        await this.SeedAsync();
        Assert.NotNull(await this.svc.SendAsync(this.mailbox.Id, new ComposeRequest { To = "not an address", Subject = "x" }));
        Assert.NotNull(await this.svc.SendAsync(this.mailbox.Id, new ComposeRequest { To = "a@b.c", Cc = "garbage" }));
        Assert.NotNull(await this.svc.SendAsync(this.mailbox.Id, new ComposeRequest { To = "a@b.c" }));
        Assert.NotNull(await this.svc.SendAsync(System.Guid.NewGuid(), new ComposeRequest { To = "a@b.c", Subject = "x" }));
    }

    [Fact]
    public async System.Threading.Tasks.Task Send_RefusedWhenMailboxFull()
    {
        await this.SeedAsync();
        await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = this.tenant.Id, LocalPart = "arun", Domain = "anjal.co.in", QuotaBytes = 10 });
        await this.store.AddMailboxUsageAsync(this.mailbox.Id, 10);
        string? error = await this.svc.SendAsync(this.mailbox.Id, new ComposeRequest { To = "a@b.c", Subject = "x" });
        Assert.NotNull(error);
        Assert.Contains("full", error, System.StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await this.store.LeaseOutboundBatchAsync(10, System.DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    [Fact]
    public void EncodeHeaderText_OnlyWhenNeeded()
    {
        Assert.Equal("plain", MailboxService.EncodeHeaderText("plain"));
        Assert.Equal("=?utf-8?B?w6k=?=", MailboxService.EncodeHeaderText("é"));
        Assert.Equal("é", EncodedWordDecoder.Decode(MailboxService.EncodeHeaderText("é")));
    }

    [Fact]
    public async System.Threading.Tasks.Task Auth_AcceptsMailboxCredentials_RejectsOthers()
    {
        await this.SeedAsync();
        var auth = new WebmailAuthService(this.store);

        var ok = await auth.AuthenticateAsync("ARUN@anjal.co.in", "correct horse battery");
        Assert.NotNull(ok);
        Assert.Equal(this.mailbox.Id, WebmailAuthService.MailboxIdOf(ok));
        Assert.Equal("arun@anjal.co.in", ok!.Identity!.Name);

        Assert.Null(await auth.AuthenticateAsync("arun@anjal.co.in", "wrong"));
        Assert.Null(await auth.AuthenticateAsync("arun+x@anjal.co.in", "correct horse battery"));
        Assert.Null(await auth.AuthenticateAsync("nobody@anjal.co.in", "correct horse battery"));
        Assert.Null(WebmailAuthService.MailboxIdOf(null));
    }
}
