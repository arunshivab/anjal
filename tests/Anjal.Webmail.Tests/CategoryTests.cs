using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

public sealed class CategoryTests : System.IDisposable
{
    private static readonly string[] ArunRecipient = new[] { "arun@anjal.co.in" };
    private static readonly int[] ExpectedSlots = new[] { 1, 2, 3, 4, 5, 6 };

    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-cat-" + System.Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly MaildirStore maildir;
    private readonly MailboxService svc;
    private TenantRow tenant = new();
    private MailboxRow mailbox = new();

    public CategoryTests()
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
        this.tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "apulki", DisplayName = "Apulki" }).ConfigureAwait(false);
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.tenant.Id, Domain = "anjal.co.in" }).ConfigureAwait(false);
        this.mailbox = await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = this.tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.co.in",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery"),
        }).ConfigureAwait(false);
        await this.svc.ListFoldersAsync(this.mailbox.Id).ConfigureAwait(false);
    }

    private async System.Threading.Tasks.Task<MessageRow> DeliverAsync(string raw)
    {
        var sink = new MailboxSink(this.store, this.maildir);
        await sink.DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "lab@apulki.in",
            EnvelopeTo = ArunRecipient,
            RawBytes = System.Text.Encoding.UTF8.GetBytes(raw),
        }).ConfigureAwait(false);
        return this.store.MailboxMessages[this.store.MailboxMessages.Count - 1];
    }

    private const string PlainMail =
        "From: Lab <lab@apulki.in>\r\nTo: arun@anjal.co.in\r\nSubject: Histopath report\r\n\r\nReport inside.\r\n";

    [Fact]
    public async System.Threading.Tasks.Task TenantDefaults_AreSixHospitalCategories_AndNotRecreatedOnceRemoved()
    {
        await this.SeedAsync();
        IReadOnlyList<CategoryRow> seeded = await this.svc.EnsureTenantDefaultsAsync(this.tenant.Id);
        Assert.Equal(6, seeded.Count);
        Assert.Equal(MailboxService.HospitalDefaults, seeded.Select(c => c.Name).ToArray());
        Assert.Equal(ExpectedSlots, seeded.Select(c => c.Slot).ToArray());
        Assert.All(seeded, c => Assert.True(c.IsShared));

        // Calling again is a no-op, even after one is removed.
        await this.store.DeleteCategoryAsync(seeded[0].Id);
        IReadOnlyList<CategoryRow> again = await this.svc.EnsureTenantDefaultsAsync(this.tenant.Id);
        Assert.Equal(5, again.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task MailboxCategories_TakeTheFreeSlots_ThenRunOutOfColour()
    {
        await this.SeedAsync();
        await this.svc.EnsureTenantDefaultsAsync(this.tenant.Id);

        Assert.Null(await this.svc.AddCategoryAsync(this.mailbox.Id, "Physics"));
        Assert.Null(await this.svc.AddCategoryAsync(this.mailbox.Id, "Teaching"));

        IReadOnlyList<CategoryView> all = await this.svc.ListCategoriesAsync(this.mailbox.Id);
        Assert.Equal(8, all.Count);
        CategoryView physics = all.First(c => c.Row.Name == "Physics");
        CategoryView teaching = all.First(c => c.Row.Name == "Teaching");
        Assert.Equal(7, physics.Row.Slot);
        Assert.Equal(8, teaching.Row.Slot);
        Assert.False(physics.Shared);
        Assert.True(all[0].Shared, "tenant defaults are listed first");

        // The ninth still works; it just has no colour.
        Assert.Null(await this.svc.AddCategoryAsync(this.mailbox.Id, "Personal"));
        IReadOnlyList<CategoryView> nine = await this.svc.ListCategoriesAsync(this.mailbox.Id);
        Assert.Equal(9, nine.Count);
        Assert.Equal(CategoryRow.NoSlot, nine.First(c => c.Row.Name == "Personal").Row.Slot);
    }

    [Fact]
    public async System.Threading.Tasks.Task AddCategory_RejectsDuplicatesBlanksAndOverlongNames()
    {
        await this.SeedAsync();
        await this.svc.EnsureTenantDefaultsAsync(this.tenant.Id);

        Assert.NotNull(await this.svc.AddCategoryAsync(this.mailbox.Id, "  "));
        Assert.NotNull(await this.svc.AddCategoryAsync(this.mailbox.Id, new string('x', 41)));

        string? sharedClash = await this.svc.AddCategoryAsync(this.mailbox.Id, "clinical");
        Assert.NotNull(sharedClash);
        Assert.Contains("organisation", sharedClash, System.StringComparison.OrdinalIgnoreCase);

        Assert.Null(await this.svc.AddCategoryAsync(this.mailbox.Id, "Physics"));
        string? ownClash = await this.svc.AddCategoryAsync(this.mailbox.Id, "PHYSICS");
        Assert.NotNull(ownClash);
        Assert.Contains("already have", ownClash, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async System.Threading.Tasks.Task SharedCategories_CannotBeRenamedOrRemovedByAMailbox()
    {
        await this.SeedAsync();
        IReadOnlyList<CategoryRow> shared = await this.svc.EnsureTenantDefaultsAsync(this.tenant.Id);

        Assert.NotNull(await this.svc.RenameCategoryAsync(this.mailbox.Id, shared[0].Id, "Renamed"));
        Assert.NotNull(await this.svc.DeleteCategoryAsync(this.mailbox.Id, shared[0].Id));
        Assert.Equal(6, (await this.store.ListCategoriesAsync(this.tenant.Id, null)).Count);

        await this.svc.AddCategoryAsync(this.mailbox.Id, "Physics");
        CategoryRow own = (await this.svc.ListCategoriesAsync(this.mailbox.Id)).First(c => !c.Shared).Row;
        Assert.Null(await this.svc.RenameCategoryAsync(this.mailbox.Id, own.Id, "Medical physics"));
        Assert.Null(await this.svc.DeleteCategoryAsync(this.mailbox.Id, own.Id));
    }

    [Fact]
    public async System.Threading.Tasks.Task AnotherMailboxsCategory_CannotBeUsed()
    {
        await this.SeedAsync();
        await this.svc.EnsureTenantDefaultsAsync(this.tenant.Id);
        MailboxRow other = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = this.tenant.Id, LocalPart = "other", Domain = "anjal.co.in" });
        await this.svc.AddCategoryAsync(other.Id, "Private");
        CategoryRow theirs = (await this.store.ListCategoriesAsync(this.tenant.Id, other.Id)).First(c => !c.IsShared);

        Assert.False(await this.svc.CanUseCategoryAsync(this.mailbox.Id, theirs.Id));
        MessageRow row = await this.DeliverAsync(PlainMail);
        Assert.False(await this.svc.CategoriseAsync(this.mailbox.Id, row.Id, theirs.Id));
        Assert.Null((await this.store.GetMessageByIdAsync(row.Id))!.CategoryId);

        // A shared one is usable by both.
        CategoryRow sharedOne = (await this.store.ListCategoriesAsync(this.tenant.Id, null))[0];
        Assert.True(await this.svc.CanUseCategoryAsync(this.mailbox.Id, sharedOne.Id));
        Assert.True(await this.svc.CanUseCategoryAsync(other.Id, sharedOne.Id));
    }

    [Fact]
    public async System.Threading.Tasks.Task Categorise_WithFutureMail_FilesTheNextMessageAutomatically()
    {
        await this.SeedAsync();
        IReadOnlyList<CategoryRow> shared = await this.svc.EnsureTenantDefaultsAsync(this.tenant.Id);
        CategoryRow diagnostics = shared.First(c => c.Name == "Diagnostics");

        MessageRow first = await this.DeliverAsync(PlainMail);
        Assert.Null(first.CategoryId);
        Assert.True(await this.svc.CategoriseAsync(this.mailbox.Id, first.Id, diagnostics.Id, alsoFutureMail: true));
        Assert.Equal(diagnostics.Id, (await this.store.GetMessageByIdAsync(first.Id))!.CategoryId);

        CategoryRuleRow rule = Assert.Single(await this.svc.ListCategoryRulesAsync(this.mailbox.Id));
        Assert.Equal("lab@apulki.in", rule.Pattern);

        MessageRow second = await this.DeliverAsync(PlainMail);
        Assert.Equal(diagnostics.Id, second.CategoryId);

        // Forgetting the rule stops it applying to later mail.
        Assert.True(await this.svc.DeleteCategoryRuleAsync(this.mailbox.Id, rule.Id));
        MessageRow third = await this.DeliverAsync(PlainMail);
        Assert.Null(third.CategoryId);
    }

    [Fact]
    public async System.Threading.Tasks.Task Categorise_ClearsWithNull_AndDeletingACategoryUncategorisesItsMail()
    {
        await this.SeedAsync();
        await this.svc.EnsureTenantDefaultsAsync(this.tenant.Id);
        await this.svc.AddCategoryAsync(this.mailbox.Id, "Physics");
        CategoryRow own = (await this.svc.ListCategoriesAsync(this.mailbox.Id)).First(c => !c.Shared).Row;

        MessageRow row = await this.DeliverAsync(PlainMail);
        Assert.True(await this.svc.CategoriseAsync(this.mailbox.Id, row.Id, own.Id));
        Assert.True(await this.svc.CategoriseAsync(this.mailbox.Id, row.Id, null));
        Assert.Null((await this.store.GetMessageByIdAsync(row.Id))!.CategoryId);

        Assert.True(await this.svc.CategoriseAsync(this.mailbox.Id, row.Id, own.Id));
        Assert.Null(await this.svc.DeleteCategoryAsync(this.mailbox.Id, own.Id));
        Assert.Null((await this.store.GetMessageByIdAsync(row.Id))!.CategoryId);
    }

    [Fact]
    public async System.Threading.Tasks.Task DomainRule_Applies_ButAnExactAddressWins()
    {
        await this.SeedAsync();
        IReadOnlyList<CategoryRow> shared = await this.svc.EnsureTenantDefaultsAsync(this.tenant.Id);
        CategoryRow vendors = shared.First(c => c.Name == "Vendors");
        CategoryRow clinical = shared.First(c => c.Name == "Clinical");

        await this.store.UpsertCategoryRuleAsync(new CategoryRuleRow { MailboxId = this.mailbox.Id, Pattern = "@apulki.in", CategoryId = vendors.Id });
        MessageRow byDomain = await this.DeliverAsync(PlainMail);
        Assert.Equal(vendors.Id, byDomain.CategoryId);

        await this.store.UpsertCategoryRuleAsync(new CategoryRuleRow { MailboxId = this.mailbox.Id, Pattern = "lab@apulki.in", CategoryId = clinical.Id });
        MessageRow byAddress = await this.DeliverAsync(PlainMail);
        Assert.Equal(clinical.Id, byAddress.CategoryId);
    }

    private static Anjal.Mime.MimePart PartOf(string mimeType)
    {
        var part = new Anjal.Mime.MimePart();
        part.Headers.Add("Content-Type", mimeType);
        return part;
    }

    [Fact]
    public void HasAttachment_ReadsTheSameSignalTheMessageViewDoes()
    {
        Anjal.Mime.MimePart text = PartOf("text/plain");
        Assert.False(MailboxSink.HasAttachment(text));

        Anjal.Mime.MimePart pdf = PartOf("application/pdf");
        Assert.True(MailboxSink.HasAttachment(pdf));

        Anjal.Mime.MimePart disposed = PartOf("text/plain");
        disposed.Headers.Add("Content-Disposition", "attachment; filename=\"notes.txt\"");
        Assert.True(MailboxSink.HasAttachment(disposed));

        var multipart = new Anjal.Mime.MimeMultipart();
        multipart.Parts.Add(PartOf("text/html"));
        Assert.False(MailboxSink.HasAttachment(multipart));
        multipart.Parts.Add(pdf);
        Assert.True(MailboxSink.HasAttachment(multipart));
        Assert.False(MailboxSink.HasAttachment(null));
    }

    [Fact]
    public async System.Threading.Tasks.Task Activity_CountsDeliveredJunkSentAttachmentsAndGroups()
    {
        await this.SeedAsync();
        IReadOnlyList<CategoryRow> shared = await this.svc.EnsureTenantDefaultsAsync(this.tenant.Id);
        CategoryRow clinical = shared.First(c => c.Name == "Clinical");

        MessageRow a = await this.DeliverAsync(PlainMail);
        await this.DeliverAsync("From: Someone <s@other.test>\r\nTo: arun@anjal.co.in\r\nSubject: Two\r\n\r\nbody\r\n");
        MessageRow junk = await this.DeliverAsync("From: Spam <x@spam.test>\r\nTo: arun@anjal.co.in\r\nSubject: Three\r\n\r\nbody\r\n");
        await this.svc.MoveAsync(this.mailbox.Id, junk.Id, Anjal.Mailbox.MailboxSink.JunkFolder);
        await this.svc.CategoriseAsync(this.mailbox.Id, a.Id, clinical.Id);
        await this.svc.SendAsync(this.mailbox.Id, new ComposeRequest { To = "someone@example.com", Subject = "Out", Body = "text" });

        MailboxActivity activity = await this.svc.GetActivityAsync(this.mailbox.Id, "30d");
        Assert.Equal(2, activity.Delivered);
        Assert.Equal(1, activity.Junked);
        Assert.Equal(1, activity.Sent);

        Assert.Contains(activity.ByFolder, f => f.Name == FolderRow.Inbox && f.Count == 2);
        Assert.Contains(activity.ByFolder, f => f.Name == "Junk" && f.Count == 1);
        Assert.DoesNotContain(activity.ByFolder, f => f.Name == "Sent");

        Assert.Contains(activity.ByCategory, c => c.Name == "Clinical" && c.Count == 1 && c.Slot == clinical.Slot);
        Assert.Contains(activity.ByCategory, c => c.Name == MailboxService.Uncategorised && c.Count == 2);

        Assert.Contains(activity.TopSenders, s => s.Address() == "lab@apulki.in");
        Assert.True(activity.TopSenders.Count <= 5);

        // Every day in the period is present, so a quiet week is a flat line.
        Assert.Equal(30, activity.ByDay.Count);
        long received = 0;
        long sent = 0;
        foreach (DailyCount d in activity.ByDay)
        {
            received += d.Received;
            sent += d.Sent;
        }
        Assert.Equal(3, received);
        Assert.Equal(1, sent);
    }

    [Fact]
    public void FillGaps_PutsAZeroOnEveryQuietDay()
    {
        var start = new System.DateTimeOffset(2026, 9, 1, 0, 0, 0, System.TimeSpan.Zero);
        var counts = new[]
        {
            new DailyCount { Day = start.AddDays(1), Received = 4, Sent = 1 },
        };
        IReadOnlyList<DailyCount> filled = MailboxService.FillGaps(counts, start, start.AddDays(5));
        Assert.Equal(5, filled.Count);
        Assert.Equal(0, filled[0].Received);
        Assert.Equal(4, filled[1].Received);
        Assert.Equal(0, filled[4].Received);
        Assert.Equal(start, filled[0].Day);
    }

    [Fact]
    public async System.Threading.Tasks.Task Activity_IsScopedToOneMailbox()
    {
        await this.SeedAsync();
        await this.DeliverAsync(PlainMail);
        MailboxRow other = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = this.tenant.Id, LocalPart = "other", Domain = "anjal.co.in" });
        MailboxActivity activity = await this.svc.GetActivityAsync(other.Id, "30d");
        Assert.Equal(0, activity.Delivered);
        Assert.Empty(activity.ByFolder);
    }
}

internal static class NamedCountExtensions
{
    /// <summary>The bare address in a top-sender row, which may be a display name plus address.</summary>
    public static string Address(this NamedCount count)
    {
        System.ArgumentNullException.ThrowIfNull(count);
        int lt = count.Name.LastIndexOf('<');
        int gt = count.Name.LastIndexOf('>');
        return lt >= 0 && gt > lt ? count.Name.Substring(lt + 1, gt - lt - 1) : count.Name;
    }
}
