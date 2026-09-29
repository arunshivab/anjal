using Anjal.Mailbox;
using Anjal.Store;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

/// <summary>
/// v1.0.0-rc.9 (DEF-076, D-59): the original of a message sent from the webmail
/// is kept as composed; the colleague's copy and the Sent copy point to it; its
/// retention clock starts only when every copy is gone; and a message whose
/// original cannot be kept is not sent.
/// </summary>
public sealed class Rc9WebmailEvidenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc9-wm-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly MaildirStore maildir;
    private MailboxRow arun = null!;
    private MailboxRow desk = null!;

    public Rc9WebmailEvidenceTests() => this.maildir = new MaildirStore(Path.Combine(this.root, "mail"), "test");

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            foreach (string f in Directory.GetFiles(this.root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }
            Directory.Delete(this.root, recursive: true);
        }
    }

    private async Task<MailboxService> ServiceAsync(string evidenceRoot)
    {
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        this.arun = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "arun", Domain = "anjal.co.in" });
        this.desk = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "desk", Domain = "anjal.co.in" });
        return new MailboxService(this.store, this.store, this.maildir, "anjal.localhost")
        {
            Evidence = new EvidenceRecorder(this.store, new EvidenceVault(evidenceRoot)),
        };
    }

    private static ComposeRequest Request() => new() { To = "desk@anjal.co.in, doctor@example.org", Subject = "Rota", Body = "Tomorrow's rota is attached." };

    [Fact]
    public async Task ASend_KeepsTheMessageAsComposed_AndTheColleaguesCopyAndTheSentCopyPointToIt()
    {
        MailboxService svc = await this.ServiceAsync(Path.Combine(this.root, "evidence"));
        Assert.Null(await svc.SendAsync(this.arun.Id, Request()));

        MessageRow deskCopy = this.store.MailboxMessages.Single(m => m.MailboxId == this.desk.Id);
        MessageRow sentCopy = this.store.MailboxMessages.Single(m => m.MailboxId == this.arun.Id);
        Assert.NotNull(deskCopy.EvidenceId);
        Assert.Equal(deskCopy.EvidenceId, sentCopy.EvidenceId);
        EvidenceRow evidence = (await this.store.GetEvidenceAsync(deskCopy.EvidenceId!.Value))!;
        Assert.Equal("accepted", evidence.Outcome);
        Assert.Equal("arun@anjal.co.in", evidence.AuthenticatedUser);
        Assert.Contains("doctor@example.org", evidence.EnvelopeTo);

        // Exactly as composed: the Sent copy is filed as composed, byte for byte.
        byte[] kept = await File.ReadAllBytesAsync(Path.Combine(this.root, "evidence", evidence.Path));
        byte[] sentBytes = (await this.maildir.ReadAsync("imagiqa", "arun@anjal.co.in", "Sent", sentCopy.MaildirFile))!;
        Assert.Equal(sentBytes, kept);
    }

    [Fact]
    public async Task TheRetentionClock_StartsOnlyWhenEveryCopyIsGone()
    {
        MailboxService svc = await this.ServiceAsync(Path.Combine(this.root, "evidence"));
        Assert.Null(await svc.SendAsync(this.arun.Id, Request()));
        MessageRow deskCopy = this.store.MailboxMessages.Single(m => m.MailboxId == this.desk.Id);
        MessageRow sentCopy = this.store.MailboxMessages.Single(m => m.MailboxId == this.arun.Id);
        Guid id = deskCopy.EvidenceId!.Value;

        await this.store.DeleteMessageAsync(deskCopy.Id);
        Assert.Null((await this.store.GetEvidenceAsync(id))!.AllCopiesDeletedAt);
        await this.store.DeleteMessageAsync(sentCopy.Id);
        Assert.NotNull((await this.store.GetEvidenceAsync(id))!.AllCopiesDeletedAt);
    }

    [Fact]
    public async Task WhenTheOriginalCannotBeKept_NothingIsSent()
    {
        Directory.CreateDirectory(this.root);
        string blocker = Path.Combine(this.root, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "a file where the evidence folder should be");
        MailboxService svc = await this.ServiceAsync(blocker);

        string? error = await svc.SendAsync(this.arun.Id, Request());

        Assert.StartsWith("Not sent: the message could not be recorded as evidence", error, StringComparison.Ordinal);
        Assert.Empty(this.store.MailboxMessages);   // no colleague's copy, no Sent copy
        Assert.Empty(this.store.Outbound);           // nothing queued
    }
}
