using System.Text;
using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

/// <summary>
/// rc.15 batch A (owner's decisions of 7 Oct 2026): "Expect a reply" and "No reply needed"
/// (item 63), people written to joining contacts and the mail between a person and a contact
/// (item 29), and Send one each's matching, fallbacks and organisation limit (item 64).
/// </summary>
public sealed class Rc15BatchATests : IDisposable
{
    private static readonly string[] ArunRecipient = { "arun@anjal.co.in" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc15a-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly MaildirStore maildir;
    private readonly MailboxService svc;
    private readonly TenantRow tenant;
    private readonly MailboxRow arun;

    public Rc15BatchATests()
    {
        this.maildir = new MaildirStore(this.root, "test");
        this.svc = new MailboxService(this.store, this.store, this.maildir, "mail.anjal.co.in");
        this.tenant = this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "Imagiqa", PostmasterMailbox = "arun@anjal.co.in" }).GetAwaiter().GetResult();
        this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.tenant.Id, Domain = "anjal.co.in" }).GetAwaiter().GetResult();
        this.arun = this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = this.tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.co.in",
            DisplayName = "Arun Shiva B",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        }).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    [Fact]
    public async Task ExpectAReply_CountsEvenAShortNote_AndNoReplyNeededTakesOneOut()
    {
        DateTimeOffset sentAt = DateTimeOffset.UtcNow;
        DateTimeOffset later = sentAt.AddDays(5);
        await this.svc.MarkExpectReplyAsync(this.arun.Id, "Ravi Kumar <ravi@supplier.example>", "Thanks");
        await this.SentAsync("Thanks", "ravi@supplier.example", "Thanks!", sentAt);
        await this.SentAsync("Quotation for October", "ravi@supplier.example", "Please send your quotation for the October order by Friday.", sentAt);

        IReadOnlyList<MessageRow> waiting = await this.svc.NoReplyMessagesAsync(this.arun.Id, later);
        Assert.Equal(2, waiting.Count);

        MessageRow quotation = waiting.Single(m => m.Subject == "Quotation for October");
        await this.svc.MarkNoReplyNeededAsync(this.arun.Id, quotation.Id);
        MessageRow left = Assert.Single(await this.svc.NoReplyMessagesAsync(this.arun.Id, later));
        Assert.Equal("Thanks", left.Subject);
        Assert.Equal(1, await this.svc.NoReplyCountAsync(this.arun.Id, later));
    }

    [Fact]
    public async Task PeopleWrittenTo_JoinContacts_NamedAsWritten_WithoutChangingAnyoneAlreadyThere()
    {
        Assert.Null(await this.svc.SaveContactAsync(this.arun.Id, new Contact { Address = "meera@clinic.example", FirstName = "Dr Meera", LastName = "Nair" }));
        var request = new ComposeRequest
        {
            To = "\"Ravi Kumar\" <ravi@supplier.example>, meera@clinic.example",
            Cc = "arun@anjal.co.in",
            Bcc = "audit@hospital.example",
            Subject = "Roster",
        };

        Assert.Equal(2, await this.svc.RememberRecipientsAsync(this.arun.Id, "arun@anjal.co.in", request));

        Contact? ravi = await this.svc.FindContactAsync(this.arun.Id, "ravi@supplier.example");
        Assert.NotNull(ravi);
        Assert.Equal("Ravi", ravi.FirstName);
        Assert.Equal("Kumar", ravi.LastName);
        Contact? audit = await this.svc.FindContactAsync(this.arun.Id, "audit@hospital.example");
        Assert.NotNull(audit);
        Assert.Equal(string.Empty, audit.FirstName);
        Assert.Equal("Dr Meera", (await this.svc.FindContactAsync(this.arun.Id, "meera@clinic.example"))!.FirstName);
        Assert.Null(await this.svc.FindContactAsync(this.arun.Id, "arun@anjal.co.in"));

        ContactSettings off = await this.svc.GetContactSettingsAsync(this.arun.Id);
        off.SaveRecipientsAutomatically = false;
        await this.svc.SetContactSettingsAsync(this.arun.Id, off);
        Assert.Equal(0, await this.svc.RememberRecipientsAsync(this.arun.Id, "arun@anjal.co.in", new ComposeRequest { To = "new@person.example" }));
        Assert.Null(await this.svc.FindContactAsync(this.arun.Id, "new@person.example"));
    }

    [Theory]
    [InlineData("Meera Iyer", "meera@x.example", "Meera", "Iyer")]
    [InlineData("\"Ravi\"", "ravi@x.example", "Ravi", "")]
    [InlineData("ravi@x.example", "ravi@x.example", "", "")]
    [InlineData("", "ravi@x.example", "", "")]
    [InlineData("Anil Kumar Sharma", "a@x.example", "Anil Kumar", "Sharma")]
    public void Names_AreSplitAtTheLastWord(string name, string address, string first, string last)
    {
        (string f, string l) = MailboxService.SplitName(name, address);
        Assert.Equal(first, f);
        Assert.Equal(last, l);
    }

    [Fact]
    public async Task MailBetweenYou_CountsSentAndReceived_LeavingJunkOut()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await this.SentAsync("Quotation", "ravi@supplier.example", "Please quote.", now.AddDays(-3));
        await this.SentAsync("Order", "\"Ravi\" <ravi@supplier.example>", "Please deliver.", now.AddDays(-2));
        await this.DeliverAsync("From: Ravi <ravi@supplier.example>\r\nTo: arun@anjal.co.in\r\nSubject: Re: Quotation\r\n\r\nAttached.\r\n");
        FolderRow junk = await this.store.EnsureFolderAsync(this.arun.Id, MailboxSink.JunkFolder);
        await this.store.SaveMessageAsync(new MessageRow
        {
            MailboxId = this.arun.Id,
            FolderId = junk.Id,
            MaildirFile = Guid.NewGuid().ToString("N"),
            EnvelopeFrom = "ravi@supplier.example",
            FromHeader = "Ravi <ravi@supplier.example>",
            ToHeader = "arun@anjal.co.in",
            Subject = "Offer",
            BodyText = "Offer",
        });

        ContactTraffic t = await this.svc.ContactTrafficAsync(this.arun.Id, "ravi@supplier.example");
        Assert.Equal(2, t.Sent);
        Assert.Equal(1, t.Received);
        Assert.NotNull(t.Last);
        Assert.False(t.LastWasSent);
    }

    [Fact]
    public void SendOneEach_FillsEachBlankFromTheMatchedField_ElseItsOwnFallback()
    {
        var person = new MergePerson("ravi@supplier.example", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Given name"] = "Ravi",
            ["Ward"] = string.Empty,
        });
        var map = new Dictionary<string, MergeBlank>(StringComparer.Ordinal)
        {
            ["First name"] = new MergeBlank("Given name", "colleague"),
            ["Ward"] = new MergeBlank("Ward", "your ward"),
            ["Note"] = new MergeBlank("-none-", "nothing new"),
        };

        Assert.Equal("Dear Ravi, about your ward: nothing new", MailboxService.MergeFill("Dear {First name}, about {Ward}: {Note}", person, map));
        Assert.Equal("Dear colleague", MailboxService.MergeFill("Dear {First name}", new MergePerson("x@y.example", new Dictionary<string, string>())));
    }

    [Fact]
    public async Task TheOrganisationsMergeLimit_StaysWithinAnjalsCeiling_AndIsWhatSendOneEachChecks()
    {
        Assert.Equal(MailboxService.DefaultDailyMergeLimit, await this.svc.MergeLimitOfAsync(this.tenant.Id));
        Assert.Equal(MailboxService.MergeCeiling, await this.svc.SaveMergeLimitAsync(this.tenant.Id, 10_000));
        Assert.Equal(1, await this.svc.SaveMergeLimitAsync(this.tenant.Id, 0));
        Assert.Equal(1, await this.svc.MergeLimitForAsync(this.arun.Id));

        (string? error, int made) = await this.svc.SendOneEachAsync(this.arun.Id, new ComposeRequest
        {
            To = "a@one.example, b@two.example",
            Subject = "Hello {First name}",
            Body = "Hi",
        }, null, DateTimeOffset.UtcNow);
        Assert.Equal(0, made);
        Assert.NotNull(error);
        Assert.Contains("allows 1 messages a day", error, StringComparison.Ordinal);
    }

    private async Task DeliverAsync(string raw)
    {
        DeliveryResult r = await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "ravi@supplier.example",
            EnvelopeTo = ArunRecipient,
            RawBytes = Encoding.UTF8.GetBytes(raw),
        });
        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
    }

    private async Task SentAsync(string subject, string to, string body, DateTimeOffset at)
    {
        FolderRow sent = await this.store.EnsureFolderAsync(this.arun.Id, "Sent");
        this.store.MessageClock = () => at;
        await this.store.SaveMessageAsync(new MessageRow
        {
            MailboxId = this.arun.Id,
            FolderId = sent.Id,
            MaildirFile = Guid.NewGuid().ToString("N"),
            FromHeader = "Arun Shiva B <arun@anjal.co.in>",
            ToHeader = to,
            Subject = subject,
            BodyText = body,
            Preview = MessageRow.PreviewOf(body),
            Seen = true,
        });
        this.store.MessageClock = () => DateTimeOffset.UtcNow;
    }
}
