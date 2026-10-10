using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

public sealed class Rc11ListServiceTests : System.IDisposable
{
    private static readonly string[] ArunRecipient = new[] { "arun@anjal.co.in" };
    private static readonly System.DateTimeOffset Now = new(2026, 10, 2, 4, 30, 0, System.TimeSpan.Zero);
    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-rc11-" + System.Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly MaildirStore maildir;
    private readonly MailboxService svc;
    private MailboxRow mailbox = new();

    public Rc11ListServiceTests()
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
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa" }).ConfigureAwait(false);
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" }).ConfigureAwait(false);
        this.mailbox = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "arun", Domain = "anjal.co.in", PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery") }).ConfigureAwait(false);
        await this.svc.ListFoldersAsync(this.mailbox.Id).ConfigureAwait(false);
    }

    private async System.Threading.Tasks.Task<MessageRow> DeliverAsync(string subject)
    {
        await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "sender@example.com",
            EnvelopeTo = ArunRecipient,
            RawBytes = System.Text.Encoding.UTF8.GetBytes("Subject: " + subject + "\r\n\r\nbody\r\n"),
        }).ConfigureAwait(false);
        return this.store.MailboxMessages[this.store.MailboxMessages.Count - 1];
    }

    private async System.Threading.Tasks.Task<string> FolderOfAsync(System.Guid messageId) =>
        (await this.store.ListFoldersAsync(this.mailbox.Id)).Single(f => f.Id == (this.store.MailboxMessages.Single(m => m.Id == messageId)).FolderId).Name;

    [Fact]
    public async System.Threading.Tasks.Task TrashThenUndo_PutsEachMessageBackWhereItWas()
    {
        // UX-07: "Undo after delete or move".
        await this.SeedAsync();
        MessageRow a = await this.DeliverAsync("A");
        MessageRow b = await this.DeliverAsync("B");

        (int moved, System.Guid? token) = await this.svc.MoveWithUndoAsync(this.mailbox.Id, new[] { a.Id, b.Id }, "Trash", Now);
        Assert.Equal(2, moved);
        Assert.Equal("Trash", await this.FolderOfAsync(a.Id));

        Assert.Equal(2, await this.svc.UndoMoveAsync(this.mailbox.Id, token!.Value, Now.AddMinutes(9)));
        Assert.Equal(FolderRow.Inbox, await this.FolderOfAsync(a.Id));
        Assert.Equal(FolderRow.Inbox, await this.FolderOfAsync(b.Id));
    }

    [Fact]
    public async System.Threading.Tasks.Task Undo_WorksOnce_OnlyForTheSameMailbox_AndOnlyWithinTenMinutes()
    {
        await this.SeedAsync();
        MessageRow a = await this.DeliverAsync("A");
        (_, System.Guid? token) = await this.svc.MoveWithUndoAsync(this.mailbox.Id, new[] { a.Id }, "Trash", Now);

        Assert.Equal(0, await this.svc.UndoMoveAsync(System.Guid.NewGuid(), token!.Value, Now));
        Assert.Equal(0, await this.svc.UndoMoveAsync(this.mailbox.Id, token.Value, Now.AddMinutes(11)));
        Assert.Equal("Trash", await this.FolderOfAsync(a.Id));

        (_, System.Guid? second) = await this.svc.MoveWithUndoAsync(this.mailbox.Id, new[] { a.Id }, FolderRow.Inbox, Now);
        Assert.Equal(1, await this.svc.UndoMoveAsync(this.mailbox.Id, second!.Value, Now));
        Assert.Equal(0, await this.svc.UndoMoveAsync(this.mailbox.Id, second.Value, Now));
    }

    [Fact]
    public async System.Threading.Tasks.Task MovingToAFolderThatDoesNotExist_MovesNothing_AndOffersNoUndo()
    {
        await this.SeedAsync();
        MessageRow a = await this.DeliverAsync("A");
        (int moved, System.Guid? token) = await this.svc.MoveWithUndoAsync(this.mailbox.Id, new[] { a.Id }, "../escape", Now);
        Assert.Equal(0, moved);
        Assert.Null(token);
        Assert.Equal(FolderRow.Inbox, await this.FolderOfAsync(a.Id));
    }

    [Fact]
    public async System.Threading.Tasks.Task PageSizeAndSound_AreSaved_AndAWrongPageSizeKeeps50()
    {
        await this.SeedAsync();
        Assert.True((await this.store.GetMailboxByIdAsync(this.mailbox.Id))!.NewMailSound);
        Assert.True(await this.svc.SetPageSizeAsync(this.mailbox.Id, 20));
        Assert.True(await this.svc.SetNewMailSoundAsync(this.mailbox.Id, false));
        MailboxRow m = (await this.store.GetMailboxByIdAsync(this.mailbox.Id))!;
        Assert.Equal(20, m.PageSize);
        Assert.False(m.NewMailSound);
        await this.svc.SetPageSizeAsync(this.mailbox.Id, 37);
        Assert.Equal(50, (await this.store.GetMailboxByIdAsync(this.mailbox.Id))!.PageSize);
    }

    [Fact]
    public async System.Threading.Tasks.Task InboxState_GivesTheUnreadCountAndTheNewestArrival()
    {
        await this.SeedAsync();
        Assert.Equal(0, (await this.svc.InboxStateAsync(this.mailbox.Id)).Unread);
        this.store.MessageClock = () => Now;
        await this.DeliverAsync("A");
        this.store.MessageClock = () => Now.AddMinutes(1);
        await this.DeliverAsync("B");
        (long unread, System.DateTimeOffset? newest) = await this.svc.InboxStateAsync(this.mailbox.Id);
        Assert.Equal(2, unread);
        Assert.Equal(Now.AddMinutes(1), newest);
    }
}
