using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Mailbox.Tests;

/// <summary>rc.12 (items 24, 31): the person's rules, judged and applied as mail arrives.</summary>
public sealed class Rc12MailRulesTests : System.IDisposable
{
    private static readonly byte[] Newsletter = System.Text.Encoding.ASCII.GetBytes(
        "From: Healthcare Weekly <newsletter@hc-weekly.example>\r\n" +
        "To: arun@anjal.co.in\r\n" +
        "Subject: This week in hospital IT\r\n" +
        "List-Unsubscribe: <mailto:leave@hc-weekly.example>\r\n" +
        "Message-ID: <n1@hc-weekly.example>\r\n" +
        "\r\n" +
        "News.\r\n");

    private static readonly byte[] Report = System.Text.Encoding.ASCII.GetBytes(
        "From: noreply-dmarc-support@google.com\r\n" +
        "To: arun@anjal.co.in\r\n" +
        "Subject: Report domain: anjal.co.in Submitter: google.com Report-ID: 123\r\n" +
        "Message-ID: <d1@google.com>\r\n" +
        "\r\n" +
        "Report attached.\r\n");

    private static readonly string[] Arun = { "arun@anjal.co.in" };

    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-rules-" + System.Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly MaildirStore maildir;

    public Rc12MailRulesTests()
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

    private async System.Threading.Tasks.Task<MailboxRow> SeedAsync()
    {
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        return await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "arun", Domain = "anjal.co.in" });
    }

    private async System.Threading.Tasks.Task<MessageRow> DeliverAsync(byte[] raw, string from)
    {
        DeliveryResult r = await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = from,
            EnvelopeTo = Arun,
            RawBytes = raw,
        });
        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
        return this.store.MailboxMessages[^1];
    }

    private async System.Threading.Tasks.Task<string> FolderOfAsync(MailboxRow mb, MessageRow m) =>
        (await this.store.ListFoldersAsync(mb.Id)).Single(f => f.Id == m.FolderId).Name;

    [Fact]
    public void Conditions_HoldAsWritten()
    {
        var mail = new RuleSubject { From = "Accounts <accounts@anjal.co.in>", FromAddress = "accounts@anjal.co.in", Subject = "Report domain: x", HasUnsubscribe = true, FromOutside = false };
        var none = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyList<string>>();
        Assert.True(MailRules.Holds(new RuleCondition { Field = "from", Op = "contains", Value = "ACCOUNTS" }, mail, none));
        Assert.True(MailRules.Holds(new RuleCondition { Field = "from", Op = "is", Value = "accounts@anjal.co.in" }, mail, none));
        Assert.True(MailRules.Holds(new RuleCondition { Field = "subject", Op = "startswith", Value = "report domain:" }, mail, none));
        Assert.False(MailRules.Holds(new RuleCondition { Field = "subject", Op = "notcontains", Value = "domain" }, mail, none));
        Assert.True(MailRules.Holds(new RuleCondition { Field = "unsubscribe" }, mail, none));
        Assert.False(MailRules.Holds(new RuleCondition { Field = "outside" }, mail, none));
        Assert.False(MailRules.Holds(new RuleCondition { Field = "from", Op = "contains", Value = "" }, mail, none));
        var groups = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyList<string>>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["Suppliers"] = new[] { "accounts@anjal.co.in" },
        };
        Assert.True(MailRules.Holds(new RuleCondition { Field = "group", Value = "suppliers" }, mail, groups));
    }

    [Fact]
    public void FirstMatchingRuleActs_OffRulesAndBrokenDocumentsDoNothing()
    {
        var mail = new RuleSubject { Subject = "Invoice 12" };
        var none = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyList<string>>();
        MailRule off = new() { Name = "off", Enabled = false, Conditions = { new RuleCondition { Field = "subject", Value = "Invoice" } }, MoveTo = "A" };
        MailRule first = new() { Name = "first", Conditions = { new RuleCondition { Field = "subject", Value = "Invoice" } }, MoveTo = "B", MarkRead = true };
        MailRule second = new() { Name = "second", Conditions = { new RuleCondition { Field = "subject", Value = "Invoice" } }, MoveTo = "C" };
        RuleOutcome? outcome = MailRules.Evaluate(new[] { off, first, second }, mail, none);
        Assert.NotNull(outcome);
        Assert.Equal("B", outcome!.MoveTo);
        Assert.True(outcome.MarkRead);
        Assert.Empty(MailRules.Parse("{ not json"));
        Assert.Empty(MailRules.Parse(null));
        Assert.Equal(first.Name, MailRules.Parse(MailRules.Write(new[] { first }))[0].Name);
    }

    [Fact]
    public async System.Threading.Tasks.Task ArrivingMail_IsFiledMarkedAndFlagged_AsTheRuleSays()
    {
        MailboxRow mb = await this.SeedAsync();
        MailRule rule = new() { Name = "Newsletters", Conditions = { new RuleCondition { Field = "unsubscribe" }, new RuleCondition { Field = "outside" } }, MoveTo = "Reading", MarkRead = true, Flag = true };
        await this.store.SetMailboxDocumentAsync(mb.Id, MailRules.Kind, MailRules.Write(new[] { rule }));

        MessageRow news = await this.DeliverAsync(Newsletter, "newsletter@hc-weekly.example");
        Assert.Equal("Reading", await this.FolderOfAsync(mb, news));
        Assert.True(news.Seen);
        Assert.True(news.Flagged);

        MessageRow other = await this.DeliverAsync(Report, "noreply-dmarc-support@google.com");
        Assert.Equal(FolderRow.Inbox, await this.FolderOfAsync(mb, other));
        Assert.False(other.Seen);
    }

    [Fact]
    public async System.Threading.Tasks.Task ARule_NeverFilesIntoSentDraftsOrJunk()
    {
        Assert.False(MailboxSink.IsRuleTarget("Sent"));
        Assert.False(MailboxSink.IsRuleTarget("drafts"));
        Assert.False(MailboxSink.IsRuleTarget("Junk"));
        Assert.False(MailboxSink.IsRuleTarget("Scheduled"));
        Assert.False(MailboxSink.IsRuleTarget(" "));
        Assert.True(MailboxSink.IsRuleTarget("DMARC reports"));

        MailboxRow mb = await this.SeedAsync();
        MailRule rule = new() { Name = "bad", Conditions = { new RuleCondition { Field = "subject", Value = "Report domain" } }, MoveTo = "Sent" };
        await this.store.SetMailboxDocumentAsync(mb.Id, MailRules.Kind, MailRules.Write(new[] { rule }));
        MessageRow m = await this.DeliverAsync(Report, "noreply-dmarc-support@google.com");
        Assert.Equal(FolderRow.Inbox, await this.FolderOfAsync(mb, m));
    }
}
