using System.Text;
using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Server.Tests;

/// <summary>
/// v1.0.0-rc.9: DEF-073 (RFC 6409 8.2, 8.3) - submission adds a missing Date and
/// Message-ID, the same in every copy; DEF-077 - a local recipient's copy of a
/// submitted message keeps its evidence link.
/// </summary>
public sealed class Rc9SubmissionTests : System.IDisposable
{
    private static readonly System.DateTimeOffset Now = new(2026, 10, 5, 4, 30, 0, System.TimeSpan.Zero);
    private static readonly string[] LocalDomains = { "anjal.co.in" };
    private static readonly string[] Recipients = { "desk@anjal.co.in", "doctor@example.org" };
    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-rc9-sub-" + System.Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();

    public void Dispose()
    {
        if (System.IO.Directory.Exists(this.root))
        {
            System.IO.Directory.Delete(this.root, recursive: true);
        }
    }

    [Fact]
    public void BothMissing_AreAddedAtTheEndOfTheHeader()
    {
        byte[] raw = Encoding.ASCII.GetBytes("Received: by x\r\nFrom: a@anjal.co.in\r\nSubject: s\r\n\r\nbody\r\n");
        string done = Encoding.ASCII.GetString(SubmissionHeaders.Complete(raw, "anjal.co.in", Now));
        Assert.Matches(@"^Received: by x\r\nFrom: a@anjal.co.in\r\nSubject: s\r\nDate: Mon, 05 Oct 2026 10:00:00 \+0530\r\nMessage-ID: <[0-9a-f]{32}@anjal.co.in>\r\n\r\nbody\r\n$", done);
    }

    [Fact]
    public void NothingMissing_ReturnsTheSameBytes()
    {
        byte[] raw = Encoding.ASCII.GetBytes("From: a@anjal.co.in\r\ndate: Mon, 05 Oct 2026 10:00:00 +0530\r\nMESSAGE-ID: <1@x>\r\n\r\nb\r\n");
        Assert.Same(raw, SubmissionHeaders.Complete(raw, "anjal.co.in", Now));
    }

    [Fact]
    public void OnlyTheMissingOne_IsAdded_EvenWithoutAFinalLineBreak()
    {
        string done = Encoding.ASCII.GetString(SubmissionHeaders.Complete(Encoding.ASCII.GetBytes("From: a@anjal.co.in\r\nMessage-ID: <1@x>"), "anjal.co.in", Now));
        Assert.Equal("From: a@anjal.co.in\r\nMessage-ID: <1@x>\r\nDate: Mon, 05 Oct 2026 10:00:00 +0530\r\n", done);
    }

    [Fact]
    public async Task ASubmittedMessage_GetsTheSameDateAndMessageIdInEveryCopy_AndTheLocalCopyKeepsItsEvidenceLink()
    {
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "arun", Domain = "anjal.co.in" });
        await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "desk", Domain = "anjal.co.in" });
        var mailboxes = new MailboxSink(this.store, new MaildirStore(this.root, "test"), _ => { });
        var sink = new SubmissionSink(mailboxes, new ServerLocalDomainResolver(LocalDomains, this.store, this.store), this.store, mailboxes, log: null, clock: () => Now);
        var evidenceId = System.Guid.NewGuid();

        DeliveryResult r = await sink.DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "arun@anjal.co.in",
            EnvelopeTo = Recipients,
            RawBytes = Encoding.ASCII.GetBytes("From: arun@anjal.co.in\r\nTo: desk@anjal.co.in, doctor@example.org\r\nSubject: s\r\n\r\nbody\r\n"),
            AuthenticatedUser = "arun@anjal.co.in",
            EvidenceId = evidenceId,
        });
        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);

        MailboxRow desk = (await this.store.ListMailboxesAsync(tenant.Id)).Single(m => m.LocalPart == "desk");
        MessageRow deskCopy = this.store.MailboxMessages.Single(m => m.MailboxId == desk.Id);
        Assert.Equal(evidenceId, deskCopy.EvidenceId);                                                         // DEF-077
        string deskRaw = Encoding.ASCII.GetString((await new MaildirStore(this.root, "test").ReadAsync("imagiqa", "desk@anjal.co.in", FolderRow.Inbox, deskCopy.MaildirFile))!);
        string queued = Encoding.ASCII.GetString(Assert.Single(this.store.Outbound).RawBytes);
        string messageId = System.Text.RegularExpressions.Regex.Match(queued, "Message-ID: (<[^>]+>)").Groups[1].Value;
        Assert.Matches("^<[0-9a-f]{32}@anjal.co.in>$", messageId);
        Assert.Contains("Date: Mon, 05 Oct 2026 10:00:00 +0530", queued, System.StringComparison.Ordinal);
        Assert.Contains("Message-ID: " + messageId, deskRaw, System.StringComparison.Ordinal);                 // the same message everywhere
    }
}
