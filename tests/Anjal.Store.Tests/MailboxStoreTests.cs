namespace Anjal.Store.Tests;

public class MailboxStoreTests
{
    private static async System.Threading.Tasks.Task<(InMemoryMessageStore Store, TenantRow Tenant, MailboxRow Mailbox)> SeedAsync()
    {
        var store = new InMemoryMessageStore();
        TenantRow tenant = await store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "imagiQa" }).ConfigureAwait(false);
        await store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" }).ConfigureAwait(false);
        MailboxRow mailbox = await store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.co.in",
            PasswordPbkdf2 = "pbkdf2$1$a$b",
        }).ConfigureAwait(false);
        return (store, tenant, mailbox);
    }

    [Fact]
    public async System.Threading.Tasks.Task Tenant_Upsert_AssignsIdAndLowercasesSlug()
    {
        var store = new InMemoryMessageStore();
        var saved = await store.UpsertTenantAsync(new TenantRow { Slug = "ImagiQa", DisplayName = "x" });

        Assert.NotEqual(System.Guid.Empty, saved.Id);
        Assert.Equal("imagiqa", saved.Slug);
        Assert.True(saved.Enabled);
    }

    [Fact]
    public async System.Threading.Tasks.Task Tenant_Upsert_SameSlug_PreservesIdAndUpdates()
    {
        var store = new InMemoryMessageStore();
        var first = await store.UpsertTenantAsync(new TenantRow { Slug = "t", DisplayName = "one" });
        var second = await store.UpsertTenantAsync(new TenantRow { Slug = "T", DisplayName = "two", Enabled = false });

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("two", second.DisplayName);
        Assert.False(second.Enabled);
        Assert.Single(await store.ListTenantsAsync());
    }

    [Fact]
    public async System.Threading.Tasks.Task Tenant_GetById_And_GetBySlug_Agree()
    {
        var store = new InMemoryMessageStore();
        var saved = await store.UpsertTenantAsync(new TenantRow { Slug = "t" });
        var bySlug = await store.GetTenantAsync("t");
        var byId = await store.GetTenantByIdAsync(saved.Id);

        Assert.NotNull(bySlug);
        Assert.NotNull(byId);
        Assert.Equal(bySlug!.Id, byId!.Id);
        Assert.Null(await store.GetTenantAsync("nope"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Tenant_Delete_CascadesDomainsMailboxesFoldersMessages()
    {
        (InMemoryMessageStore store, TenantRow tenant, MailboxRow mailbox) = await SeedAsync();
        FolderRow inbox = await store.EnsureFolderAsync(mailbox.Id, FolderRow.Inbox);
        await store.SaveMessageAsync(new MessageRow { MailboxId = mailbox.Id, FolderId = inbox.Id, MaildirFile = "new/x" });

        Assert.True(await store.DeleteTenantAsync(tenant.Slug));

        Assert.Null(await store.GetTenantAsync(tenant.Slug));
        Assert.Null(await store.GetTenantDomainAsync("anjal.co.in"));
        Assert.Null(await store.GetMailboxAsync("arun", "anjal.co.in"));
        Assert.Empty(await store.ListFoldersAsync(mailbox.Id));
        Assert.Equal(0, await store.CountMessagesAsync(mailbox.Id, null));
        Assert.False(await store.DeleteTenantAsync(tenant.Slug));
    }

    [Fact]
    public async System.Threading.Tasks.Task TenantDomain_Upsert_LowercasesAndIsUniqueAcrossTenants()
    {
        var store = new InMemoryMessageStore();
        var a = await store.UpsertTenantAsync(new TenantRow { Slug = "a" });
        var b = await store.UpsertTenantAsync(new TenantRow { Slug = "b" });

        var first = await store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = a.Id, Domain = "Example.COM" });
        var second = await store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = b.Id, Domain = "example.com" });

        Assert.Equal("example.com", first.Domain);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(b.Id, second.TenantId);
        Assert.Single(await store.ListTenantDomainsAsync());
        Assert.Empty(await store.ListTenantDomainsAsync(a.Id));
        Assert.Single(await store.ListTenantDomainsAsync(b.Id));
    }

    [Fact]
    public async System.Threading.Tasks.Task TenantDomain_Delete_Works()
    {
        (InMemoryMessageStore store, _, _) = await SeedAsync();
        Assert.True(await store.DeleteTenantDomainAsync("ANJAL.CO.IN"));
        Assert.False(await store.DeleteTenantDomainAsync("anjal.co.in"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Mailbox_Upsert_AssignsIdLowercasesAndDefaultsQuota()
    {
        (InMemoryMessageStore store, TenantRow tenant, MailboxRow mailbox) = await SeedAsync();

        Assert.NotEqual(System.Guid.Empty, mailbox.Id);
        Assert.Equal("arun@anjal.co.in", mailbox.Address);
        Assert.Equal(MailboxRow.DefaultQuotaBytes, mailbox.QuotaBytes);
        Assert.Equal(0, mailbox.UsedBytes);

        var again = await store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = "ARUN",
            Domain = "Anjal.Co.In",
            DisplayName = "Arun",
            PasswordPbkdf2 = string.Empty,
        });
        Assert.Equal(mailbox.Id, again.Id);
        Assert.Equal("Arun", again.DisplayName);
        Assert.Equal("pbkdf2$1$a$b", again.PasswordPbkdf2);
    }

    [Fact]
    public async System.Threading.Tasks.Task Mailbox_Upsert_NonEmptyPassword_Replaces()
    {
        (InMemoryMessageStore store, TenantRow tenant, MailboxRow mailbox) = await SeedAsync();
        var again = await store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.co.in",
            PasswordPbkdf2 = "pbkdf2$2$c$d",
        });
        Assert.Equal(mailbox.Id, again.Id);
        Assert.Equal("pbkdf2$2$c$d", again.PasswordPbkdf2);
    }

    [Fact]
    public async System.Threading.Tasks.Task Mailbox_Usage_AddsAndClampsAtZero()
    {
        (InMemoryMessageStore store, _, MailboxRow mailbox) = await SeedAsync();

        Assert.Equal(100, await store.AddMailboxUsageAsync(mailbox.Id, 100));
        Assert.Equal(150, await store.AddMailboxUsageAsync(mailbox.Id, 50));
        Assert.Equal(0, await store.AddMailboxUsageAsync(mailbox.Id, -500));
        Assert.Null(await store.AddMailboxUsageAsync(System.Guid.NewGuid(), 1));

        MailboxRow? after = await store.GetMailboxByIdAsync(mailbox.Id);
        Assert.Equal(0, after!.UsedBytes);
    }

    [Fact]
    public async System.Threading.Tasks.Task Mailbox_List_FiltersByTenant_AndSorts()
    {
        var store = new InMemoryMessageStore();
        var a = await store.UpsertTenantAsync(new TenantRow { Slug = "a" });
        var b = await store.UpsertTenantAsync(new TenantRow { Slug = "b" });
        await store.UpsertMailboxAsync(new MailboxRow { TenantId = a.Id, LocalPart = "zed", Domain = "a.test" });
        await store.UpsertMailboxAsync(new MailboxRow { TenantId = a.Id, LocalPart = "amy", Domain = "a.test" });
        await store.UpsertMailboxAsync(new MailboxRow { TenantId = b.Id, LocalPart = "bob", Domain = "b.test" });

        var all = await store.ListMailboxesAsync();
        Assert.Equal(3, all.Count);
        Assert.Equal("amy", all[0].LocalPart);
        Assert.Equal("zed", all[1].LocalPart);
        Assert.Equal("bob", all[2].LocalPart);

        Assert.Equal(2, (await store.ListMailboxesAsync(a.Id)).Count);
        Assert.Single(await store.ListMailboxesAsync(b.Id));
    }

    [Fact]
    public async System.Threading.Tasks.Task Mailbox_Delete_CascadesFoldersAndMessages()
    {
        (InMemoryMessageStore store, _, MailboxRow mailbox) = await SeedAsync();
        FolderRow inbox = await store.EnsureFolderAsync(mailbox.Id, FolderRow.Inbox);
        await store.SaveMessageAsync(new MessageRow { MailboxId = mailbox.Id, FolderId = inbox.Id, MaildirFile = "new/x" });

        Assert.True(await store.DeleteMailboxAsync("arun", "anjal.co.in"));
        Assert.Empty(await store.ListFoldersAsync(mailbox.Id));
        Assert.Empty(store.MailboxMessages);
        Assert.False(await store.DeleteMailboxAsync("arun", "anjal.co.in"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Folder_Ensure_IsIdempotent_AndInboxSortsFirst()
    {
        (InMemoryMessageStore store, _, MailboxRow mailbox) = await SeedAsync();
        var trash = await store.EnsureFolderAsync(mailbox.Id, "Trash");
        var inbox1 = await store.EnsureFolderAsync(mailbox.Id, FolderRow.Inbox);
        var inbox2 = await store.EnsureFolderAsync(mailbox.Id, FolderRow.Inbox);
        var archive = await store.EnsureFolderAsync(mailbox.Id, "Archive");

        Assert.Equal(inbox1.Id, inbox2.Id);
        var folders = await store.ListFoldersAsync(mailbox.Id);
        Assert.Equal(3, folders.Count);
        Assert.Equal(FolderRow.Inbox, folders[0].Name);
        Assert.Equal(archive.Id, folders[1].Id);
        Assert.Equal(trash.Id, folders[2].Id);
    }

    [Fact]
    public void Folder_MaildirNameFor_InboxIsRoot_OthersDotPrefixed()
    {
        Assert.Equal(string.Empty, FolderRow.MaildirNameFor(FolderRow.Inbox));
        Assert.Equal(".Sent", FolderRow.MaildirNameFor("Sent"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Message_Save_AssignsIdAndReceivedAt_AndPagesNewestFirst()
    {
        (InMemoryMessageStore store, _, MailboxRow mailbox) = await SeedAsync();
        FolderRow inbox = await store.EnsureFolderAsync(mailbox.Id, FolderRow.Inbox);
        FolderRow sent = await store.EnsureFolderAsync(mailbox.Id, "Sent");

        var m1 = await store.SaveMessageAsync(new MessageRow { MailboxId = mailbox.Id, FolderId = inbox.Id, MaildirFile = "new/1", Subject = "one" });
        var m2 = await store.SaveMessageAsync(new MessageRow { MailboxId = mailbox.Id, FolderId = inbox.Id, MaildirFile = "new/2", Subject = "two" });
        var m3 = await store.SaveMessageAsync(new MessageRow { MailboxId = mailbox.Id, FolderId = sent.Id, MaildirFile = "new/3", Subject = "three" });

        Assert.NotEqual(System.Guid.Empty, m1.Id);
        Assert.NotEqual(default, m1.ReceivedAt);

        var inboxPage = await store.ListMessagesAsync(mailbox.Id, inbox.Id, 10, 0);
        Assert.Equal(2, inboxPage.Count);
        Assert.Equal(m2.Id, inboxPage[0].Id);
        Assert.Equal(m1.Id, inboxPage[1].Id);

        var all = await store.ListMessagesAsync(mailbox.Id, null, 10, 0);
        Assert.Equal(3, all.Count);
        Assert.Equal(m3.Id, all[0].Id);

        var second = await store.ListMessagesAsync(mailbox.Id, null, 1, 1);
        Assert.Single(second);
        Assert.Equal(m2.Id, second[0].Id);

        Assert.Equal(3, await store.CountMessagesAsync(mailbox.Id, null));
        Assert.Equal(1, await store.CountMessagesAsync(mailbox.Id, sent.Id));
        Assert.NotNull(await store.GetMessageByIdAsync(m3.Id));
        Assert.Null(await store.GetMessageByIdAsync(System.Guid.NewGuid()));
    }

    [Fact]
    public async System.Threading.Tasks.Task Message_SetFlags_UpdatesFlagsAndOptionallyFile()
    {
        (InMemoryMessageStore store, _, MailboxRow mailbox) = await SeedAsync();
        FolderRow inbox = await store.EnsureFolderAsync(mailbox.Id, FolderRow.Inbox);
        var m = await store.SaveMessageAsync(new MessageRow { MailboxId = mailbox.Id, FolderId = inbox.Id, MaildirFile = "new/1" });

        MessageRow? flagged = await store.SetMessageFlagsAsync(m.Id, seen: true, flagged: true, answered: false, maildirFile: null);
        Assert.NotNull(flagged);
        Assert.True(flagged!.Seen);
        Assert.True(flagged.Flagged);
        Assert.False(flagged.Answered);
        Assert.Equal("new/1", flagged.MaildirFile);

        MessageRow? renamed = await store.SetMessageFlagsAsync(m.Id, seen: true, flagged: false, answered: true, maildirFile: "cur/1:2,RS");
        Assert.Equal("cur/1:2,RS", renamed!.MaildirFile);
        Assert.False(renamed.Flagged);
        Assert.True(renamed.Answered);

        Assert.Null(await store.SetMessageFlagsAsync(System.Guid.NewGuid(), true, true, true, null));
    }

    [Fact]
    public async System.Threading.Tasks.Task Message_Move_ChangesFolderAndFile()
    {
        (InMemoryMessageStore store, _, MailboxRow mailbox) = await SeedAsync();
        FolderRow inbox = await store.EnsureFolderAsync(mailbox.Id, FolderRow.Inbox);
        FolderRow trash = await store.EnsureFolderAsync(mailbox.Id, "Trash");
        var m = await store.SaveMessageAsync(new MessageRow { MailboxId = mailbox.Id, FolderId = inbox.Id, MaildirFile = "cur/1:2,S" });

        MessageRow? moved = await store.MoveMessageAsync(m.Id, trash.Id, "cur/1:2,S");
        Assert.Equal(trash.Id, moved!.FolderId);
        Assert.Equal(0, await store.CountMessagesAsync(mailbox.Id, inbox.Id));
        Assert.Equal(1, await store.CountMessagesAsync(mailbox.Id, trash.Id));
        Assert.Null(await store.MoveMessageAsync(System.Guid.NewGuid(), trash.Id, "x"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Message_Delete_RemovesRow()
    {
        (InMemoryMessageStore store, _, MailboxRow mailbox) = await SeedAsync();
        FolderRow inbox = await store.EnsureFolderAsync(mailbox.Id, FolderRow.Inbox);
        var m = await store.SaveMessageAsync(new MessageRow { MailboxId = mailbox.Id, FolderId = inbox.Id, MaildirFile = "new/1" });

        Assert.True(await store.DeleteMessageAsync(m.Id));
        Assert.False(await store.DeleteMessageAsync(m.Id));
        Assert.Null(await store.GetMessageByIdAsync(m.Id));
    }
}
