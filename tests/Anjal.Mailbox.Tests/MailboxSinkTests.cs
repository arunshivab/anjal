using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Mailbox.Tests;

public sealed class MailboxSinkTests : System.IDisposable
{
    private static readonly byte[] Sample = System.Text.Encoding.ASCII.GetBytes(
        "From: Sender <sender@example.com>\r\n" +
        "To: arun@anjal.co.in\r\n" +
        "Subject: Hello there\r\n" +
        "Date: Thu, 17 Sep 2026 10:00:00 +0530\r\n" +
        "Message-ID: <m1@example.com>\r\n" +
        "\r\n" +
        "Body line.\r\n");

    private static readonly byte[] Garbage = new byte[] { 0xFF, 0xFE, 0x00, 0x01 };

    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-sink-" + System.Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly MaildirStore maildir;
    private readonly System.Collections.Generic.List<string> log = new();

    public MailboxSinkTests()
    {
        this.maildir = new MaildirStore(this.root, "test");
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(this.root))
        {
            System.IO.Directory.Delete(this.root, recursive: true);
        }
    }

    private async System.Threading.Tasks.Task<(TenantRow Tenant, MailboxRow Mailbox)> SeedAsync(bool tenantEnabled = true, bool mailboxEnabled = true)
    {
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", Enabled = tenantEnabled }).ConfigureAwait(false);
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" }).ConfigureAwait(false);
        MailboxRow mailbox = await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.co.in",
            Enabled = mailboxEnabled,
        }).ConfigureAwait(false);
        return (tenant, mailbox);
    }

    private MailboxSink Sink() => new(this.store, this.maildir, this.log.Add);

    private static DeliveryContext Ctx(byte[] raw, params string[] rcpts) => new()
    {
        EnvelopeFrom = "sender@example.com",
        EnvelopeTo = rcpts,
        RawBytes = raw,
    };

    [Fact]
    public void TrySplitAddress_StripsTagAndLowercases()
    {
        Assert.True(MailboxSink.TrySplitAddress("Arun+News@Anjal.CO.IN", out string local, out string domain));
        Assert.Equal("arun", local);
        Assert.Equal("anjal.co.in", domain);
        Assert.False(MailboxSink.TrySplitAddress("no-at-sign", out _, out _));
        Assert.False(MailboxSink.TrySplitAddress("@domain", out _, out _));
        Assert.False(MailboxSink.TrySplitAddress("user@", out _, out _));
    }

    [Fact]
    public async System.Threading.Tasks.Task Deliver_KnownMailbox_WritesMaildirAndIndexRow()
    {
        (TenantRow tenant, MailboxRow mailbox) = await this.SeedAsync();
        DeliveryResult r = await this.Sink().DeliverAsync(Ctx(Sample, "arun@anjal.co.in"));

        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
        Assert.Single(this.store.MailboxMessages);
        MessageRow row = this.store.MailboxMessages[0];
        Assert.Equal(mailbox.Id, row.MailboxId);
        Assert.Equal("Hello there", row.Subject);
        Assert.Equal("m1@example.com", row.MessageId);
        Assert.Equal("Sender <sender@example.com>", row.FromHeader);
        Assert.Equal("sender@example.com", row.EnvelopeFrom);
        Assert.Equal(Sample.Length, row.SizeBytes);

        byte[]? back = await this.maildir.ReadAsync(tenant.Slug, mailbox.Address, FolderRow.Inbox, row.MaildirFile);
        Assert.Equal(Sample, back);

        MailboxRow? after = await this.store.GetMailboxByIdAsync(mailbox.Id);
        Assert.Equal(Sample.Length, after!.UsedBytes);

        var folders = await this.store.ListFoldersAsync(mailbox.Id);
        Assert.Single(folders);
        Assert.Equal(FolderRow.Inbox, folders[0].Name);
    }

    [Fact]
    public async System.Threading.Tasks.Task Deliver_PlusTag_ResolvesToBaseMailbox()
    {
        await this.SeedAsync();
        DeliveryResult r = await this.Sink().DeliverAsync(Ctx(Sample, "arun+billing@anjal.co.in"));
        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
        Assert.Single(this.store.MailboxMessages);
    }

    [Fact]
    public async System.Threading.Tasks.Task Deliver_SameMailboxTwiceInEnvelope_StoresOnce()
    {
        await this.SeedAsync();
        DeliveryResult r = await this.Sink().DeliverAsync(Ctx(Sample, "arun@anjal.co.in", "ARUN+x@anjal.co.in"));
        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
        Assert.Single(this.store.MailboxMessages);
    }

    [Fact]
    public async System.Threading.Tasks.Task Deliver_UnknownMailbox_PermanentFailure()
    {
        await this.SeedAsync();
        DeliveryResult r = await this.Sink().DeliverAsync(Ctx(Sample, "nobody@anjal.co.in"));
        Assert.Equal(DeliveryOutcome.PermanentFailure, r.Outcome);
        Assert.Empty(this.store.MailboxMessages);
    }

    [Fact]
    public async System.Threading.Tasks.Task Deliver_UnknownDomain_PermanentFailure()
    {
        await this.SeedAsync();
        DeliveryResult r = await this.Sink().DeliverAsync(Ctx(Sample, "arun@other.test"));
        Assert.Equal(DeliveryOutcome.PermanentFailure, r.Outcome);
    }

    [Fact]
    public async System.Threading.Tasks.Task Deliver_DisabledTenant_PermanentFailure()
    {
        await this.SeedAsync(tenantEnabled: false);
        DeliveryResult r = await this.Sink().DeliverAsync(Ctx(Sample, "arun@anjal.co.in"));
        Assert.Equal(DeliveryOutcome.PermanentFailure, r.Outcome);
    }

    [Fact]
    public async System.Threading.Tasks.Task Deliver_DisabledMailbox_PermanentFailure()
    {
        await this.SeedAsync(mailboxEnabled: false);
        DeliveryResult r = await this.Sink().DeliverAsync(Ctx(Sample, "arun@anjal.co.in"));
        Assert.Equal(DeliveryOutcome.PermanentFailure, r.Outcome);
    }

    [Fact]
    public async System.Threading.Tasks.Task Deliver_MixedRecipients_AcceptsKnownSkipsUnknown()
    {
        await this.SeedAsync();
        DeliveryResult r = await this.Sink().DeliverAsync(Ctx(Sample, "nobody@anjal.co.in", "arun@anjal.co.in"));
        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
        Assert.Single(this.store.MailboxMessages);
        Assert.Contains(this.log, l => l.Contains("no mailbox for nobody@anjal.co.in", System.StringComparison.Ordinal));
    }

    [Fact]
    public async System.Threading.Tasks.Task Deliver_NoRecipients_PermanentFailure()
    {
        DeliveryResult r = await this.Sink().DeliverAsync(Ctx(Sample));
        Assert.Equal(DeliveryOutcome.PermanentFailure, r.Outcome);
    }

    [Fact]
    public async System.Threading.Tasks.Task Deliver_UnparseableBytes_StillStored()
    {
        (TenantRow tenant, MailboxRow mailbox) = await this.SeedAsync();
        DeliveryResult r = await this.Sink().DeliverAsync(Ctx(Garbage, "arun@anjal.co.in"));

        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
        MessageRow row = Assert.Single(this.store.MailboxMessages);
        byte[]? back = await this.maildir.ReadAsync(tenant.Slug, mailbox.Address, FolderRow.Inbox, row.MaildirFile);
        Assert.Equal(Garbage, back);
    }

    [Fact]
    public async System.Threading.Tasks.Task Deliver_OverSoftQuota_StillAcceptsAndLogs()
    {
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "t" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "q.test" });
        await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "small", Domain = "q.test", QuotaBytes = 10 });

        DeliveryResult r = await this.Sink().DeliverAsync(Ctx(Sample, "small@q.test"));
        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
        Assert.Contains(this.log, l => l.Contains("over quota", System.StringComparison.Ordinal));
    }

    private static readonly byte[] Scored7 = System.Text.Encoding.ASCII.GetBytes(
        "X-Anjal-Spam-Score: 7\r\nX-Anjal-Spam-Reasons: SPF_FAIL(3), DKIM_FAIL(3), NO_DATE(1)\r\n" +
        "From: Spammer <spam@spammer.test>\r\nSubject: buy\r\n\r\nbody\r\n");

    [Fact]
    public async System.Threading.Tasks.Task Deliver_ScoreAtThreshold_FilesInJunk_AndRecordsScore()
    {
        await this.SeedAsync();
        DeliveryResult r = await this.Sink().DeliverAsync(Ctx(Scored7, "arun@anjal.co.in"));
        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);

        MessageRow row = Assert.Single(this.store.MailboxMessages);
        Assert.Equal(7, row.SpamScore);
        var folders = await this.store.ListFoldersAsync(row.MailboxId);
        Assert.Equal(MailboxSink.JunkFolder, Assert.Single(folders).Name);
        Assert.Contains(this.log, l => l.Contains("/.Junk/", System.StringComparison.Ordinal));
    }

    [Fact]
    public async System.Threading.Tasks.Task Deliver_ScoreBelowTenantThreshold_StaysInInbox()
    {
        (TenantRow tenant, _) = await this.SeedAsync();
        await this.store.UpsertTenantAsync(new TenantRow { Slug = tenant.Slug, SpamThreshold = 8 });
        await this.Sink().DeliverAsync(Ctx(Scored7, "arun@anjal.co.in"));
        var folders = await this.store.ListFoldersAsync(this.store.MailboxMessages[0].MailboxId);
        Assert.Equal(FolderRow.Inbox, Assert.Single(folders).Name);
    }

    [Fact]
    public async System.Threading.Tasks.Task Deliver_SenderRules_OverrideScore()
    {
        (TenantRow tenant, _) = await this.SeedAsync();
        await this.store.UpsertSenderRuleAsync(new SenderRuleRow { TenantId = tenant.Id, Pattern = "@spammer.test", Action = SenderRuleAction.Allow });
        await this.Sink().DeliverAsync(Ctx(Scored7, "arun@anjal.co.in"));
        Assert.Equal(FolderRow.Inbox, Assert.Single(await this.store.ListFoldersAsync(this.store.MailboxMessages[0].MailboxId)).Name);

        await this.store.UpsertSenderRuleAsync(new SenderRuleRow { TenantId = tenant.Id, Pattern = "sender@example.com", Action = SenderRuleAction.Block });
        await this.Sink().DeliverAsync(Ctx(Sample, "arun@anjal.co.in"));
        Assert.Equal(2, this.store.MailboxMessages.Count);
        var folders = await this.store.ListFoldersAsync(this.store.MailboxMessages[1].MailboxId);
        Assert.Contains(folders, f => f.Name == MailboxSink.JunkFolder);
        Assert.Equal(1, await this.store.CountMessagesAsync(this.store.MailboxMessages[1].MailboxId, folders.First(f => f.Name == MailboxSink.JunkFolder).Id));
    }

    [Fact]
    public void ChooseFolder_Rules()
    {
        Assert.Equal(FolderRow.Inbox, MailboxSink.ChooseFolder(0, 5, null));
        Assert.Equal(MailboxSink.JunkFolder, MailboxSink.ChooseFolder(5, 5, null));
        Assert.Equal(FolderRow.Inbox, MailboxSink.ChooseFolder(50, 0, null));
        Assert.Equal(FolderRow.Inbox, MailboxSink.ChooseFolder(50, 5, SenderRuleAction.Allow));
        Assert.Equal(MailboxSink.JunkFolder, MailboxSink.ChooseFolder(0, 5, SenderRuleAction.Block));
    }

    [Fact]
    public async System.Threading.Tasks.Task Deliver_ThroughSmtpServer_EndToEnd()
    {
        await this.SeedAsync();
        var options = new SmtpServerOptions
        {
            BindAddress = System.Net.IPAddress.Loopback,
            Port = 0,
            AdvertisedHostName = "anjal.localhost",
        };
        using var cts = new System.Threading.CancellationTokenSource();
        using var server = new SmtpServer(options, this.Sink());
        System.Threading.Tasks.Task serverTask = server.StartAsync(cts.Token);
        await System.Threading.Tasks.Task.Delay(100);

        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, server.BoundPort);
        using var net = client.GetStream();
        var reader = new System.IO.StreamReader(net, System.Text.Encoding.ASCII);
        var writer = new System.IO.StreamWriter(net, System.Text.Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

        await reader.ReadLineAsync();
        await writer.WriteLineAsync("EHLO c");
        string? line;
        do
        {
            line = await reader.ReadLineAsync();
        }
        while (line is not null && line.Length >= 4 && line[3] == '-');
        await writer.WriteLineAsync("MAIL FROM:<sender@example.com>");
        Assert.StartsWith("250", await reader.ReadLineAsync(), System.StringComparison.Ordinal);
        await writer.WriteLineAsync("RCPT TO:<arun@anjal.co.in>");
        Assert.StartsWith("250", await reader.ReadLineAsync(), System.StringComparison.Ordinal);
        await writer.WriteLineAsync("DATA");
        Assert.StartsWith("354", await reader.ReadLineAsync(), System.StringComparison.Ordinal);
        await writer.WriteAsync(System.Text.Encoding.ASCII.GetString(Sample));
        await writer.WriteLineAsync(".");
        string? dataReply = await reader.ReadLineAsync();
        Assert.StartsWith("250", dataReply, System.StringComparison.Ordinal);
        await writer.WriteLineAsync("QUIT");

        cts.Cancel();
        try
        {
            await serverTask;
        }
        catch (System.OperationCanceledException)
        {
        }

        Assert.Single(this.store.MailboxMessages);
    }
    // ---- Decision 1B (v1.0.0-rc.5): whether a message went through the incoming checks ----

    private static byte[] Scored(int score) => System.Text.Encoding.ASCII.GetBytes(
        $"X-Anjal-Spam-Score: {score}\r\nX-Anjal-Spam-Reasons: none\r\n").Concat(Sample).ToArray();

    [Fact]
    public async System.Threading.Tasks.Task Deliver_ScoredMailFromOutside_IsRecordedAsChecked()
    {
        await this.SeedAsync();
        await this.Sink().DeliverAsync(Ctx(Scored(0), "arun@anjal.co.in"));

        MessageRow row = Assert.Single(this.store.MailboxMessages);
        Assert.True(row.SpamChecked);
        Assert.Equal(0, row.SpamScore);
    }

    [Fact]
    public async System.Threading.Tasks.Task Deliver_FromASignedInAccount_IsNotChecked_EvenWithAScoreHeaderItWroteItself()
    {
        await this.SeedAsync();
        DeliveryContext ctx = new()
        {
            EnvelopeFrom = "colleague@anjal.co.in",
            EnvelopeTo = new[] { "arun@anjal.co.in" },
            RawBytes = Scored(0),
            AuthenticatedUser = "colleague@anjal.co.in",
        };
        await this.Sink().DeliverAsync(ctx);

        Assert.False(Assert.Single(this.store.MailboxMessages).SpamChecked);
    }

    [Fact]
    public async System.Threading.Tasks.Task Deliver_MailTheFilterNeverSaw_IsNotChecked()
    {
        await this.SeedAsync();
        await this.Sink().DeliverAsync(Ctx(Sample, "arun@anjal.co.in"));

        Assert.False(Assert.Single(this.store.MailboxMessages).SpamChecked);
    }

    [Fact]
    public async System.Threading.Tasks.Task SentCopy_IsNotChecked()
    {
        await this.SeedAsync();
        Assert.True(await this.Sink().FileSentCopyAsync("arun@anjal.co.in", Sample));

        Assert.False(Assert.Single(this.store.MailboxMessages).SpamChecked);
    }
}
