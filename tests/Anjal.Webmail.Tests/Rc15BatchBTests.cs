using System.Text;
using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

/// <summary>
/// rc.15 batch B (owner's decisions of 7 Oct 2026). Item 22: Trash and Junk days - people who
/// never chose follow the organisation's setting, including later changes; own choices are kept
/// within the range the organisation allows; 90 days is Anjal's outer limit; an organisation can
/// only make the rules stricter.
/// </summary>
public sealed class Rc15BatchBTests : IDisposable
{
    private static readonly string[] ArunRecipient = { "arun@anjal.co.in" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc15b-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly MaildirStore maildir;
    private readonly MailboxService svc;
    private readonly TenantRow tenant;
    private readonly MailboxRow arun;

    public Rc15BatchBTests()
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
    public async Task SomeoneWhoNeverChose_FollowsTheOrganisation_IncludingLaterChanges()
    {
        TrashRule first = await this.svc.TrashRuleForAsync(this.arun.Id);
        Assert.False(first.Chosen);
        Assert.Equal(MailboxService.DefaultTrashDays, first.Days);

        Assert.Null(await this.svc.SaveRetentionAsync(this.tenant, new RetentionPolicy { TrashDays = 14, TrashMaxDays = 60, JunkDays = 30 }));
        TrashRule later = await this.svc.TrashRuleForAsync(this.arun.Id);
        Assert.Equal(14, later.Days);
        Assert.Equal(60, later.Longest);
        Assert.Equal(30, later.JunkDays);
    }

    [Fact]
    public async Task AnOwnChoice_IsKeptWithinTheOrganisationsRange_AndCanBeGivenBack()
    {
        Assert.Null(await this.svc.SetMailSettingsAsync(this.arun.Id, null, 60));
        Assert.Equal(60, (await this.svc.TrashRuleForAsync(this.arun.Id)).Days);

        // The organisation makes it stricter: the choice is kept, within the new range.
        Assert.Null(await this.svc.SaveRetentionAsync(this.tenant, new RetentionPolicy { TrashDays = 14, TrashMaxDays = 30 }));
        TrashRule held = await this.svc.TrashRuleForAsync(this.arun.Id);
        Assert.True(held.Chosen);
        Assert.Equal(30, held.Days);
        Assert.NotNull(await this.svc.SetMailSettingsAsync(this.arun.Id, null, 60));

        // "Same as my organisation" follows it again.
        Assert.Null(await this.svc.SetMailSettingsAsync(this.arun.Id, null, 0));
        TrashRule back = await this.svc.TrashRuleForAsync(this.arun.Id);
        Assert.False(back.Chosen);
        Assert.Equal(14, back.Days);
    }

    [Fact]
    public async Task AnOrganisation_CanOnlyBeStricter_ThanAnjal()
    {
        Assert.NotNull(await this.svc.SaveRetentionAsync(this.tenant, new RetentionPolicy { TrashDays = 30, TrashMaxDays = 120 }));
        Assert.NotNull(await this.svc.SaveRetentionAsync(this.tenant, new RetentionPolicy { TrashDays = 30, TrashMaxDays = 14 }));
        Assert.NotNull(await this.svc.SaveRetentionAsync(this.tenant, new RetentionPolicy { TrashDays = 120, TrashMaxDays = 120 }));
        Assert.NotNull(await this.svc.SaveRetentionAsync(this.tenant, new RetentionPolicy { JunkDays = 120 }));
        Assert.Null(await this.svc.SaveRetentionAsync(this.tenant, new RetentionPolicy { TrashDays = 90, TrashMaxDays = 90, JunkDays = 90 }));
    }

    [Fact]
    public async Task Emptying_UsesTheDaysThatApplyToThePerson()
    {
        Assert.Null(await this.svc.SaveRetentionAsync(this.tenant, new RetentionPolicy { TrashDays = 7, TrashMaxDays = 30 }));
        await this.DeliverAsync("From: a@x.test\r\nTo: arun@anjal.co.in\r\nSubject: Old\r\n\r\nx\r\n");
        FolderRow inbox = await this.store.EnsureFolderAsync(this.arun.Id, "INBOX");
        MessageRow m = (await this.store.ListMessagesAsync(this.arun.Id, inbox.Id, 10, 0)).Single();
        Assert.NotNull(await this.svc.MoveAsync(this.arun.Id, m.Id, "Trash"));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.Equal(0, await this.svc.EmptyOldAsync(this.arun.Id, now));               // first seen in Trash now
        Assert.Equal(0, await this.svc.EmptyOldAsync(this.arun.Id, now.AddDays(6)));
        Assert.Equal(1, await this.svc.EmptyOldAsync(this.arun.Id, now.AddDays(8)));    // the organisation's 7 days
    }

    private async Task DeliverAsync(string raw)
    {
        DeliveryResult r = await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "a@x.test",
            EnvelopeTo = ArunRecipient,
            RawBytes = Encoding.UTF8.GetBytes(raw),
        });
        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
    }
}
