using System.Security.Cryptography;
using System.Text;
using Anjal.Dkim;
using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Server.Tests;

/// <summary>
/// v1.0.0-rc.8, SPEC-08 R-05 and R-08: outgoing mail is signed once, kept
/// exactly as transmitted, every retry sends the identical bytes, every
/// attempt and the receiving server's reply is recorded - and nothing is sent
/// when the copy cannot be kept.
/// </summary>
public sealed class Rc8OutboundEvidenceTests : System.IDisposable
{
    private const string Message =
        "From: Arun <arun@anjal.co.in>\r\nTo: doctor@example.org\r\nSubject: Report\r\n" +
        "Date: Mon, 28 Sep 2026 10:00:00 +0530\r\nMessage-ID: <o1@anjal.co.in>\r\n\r\nBody.\r\n";

    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-rc8-out-" + System.Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private System.DateTimeOffset now = new(2026, 9, 28, 4, 30, 0, System.TimeSpan.Zero);

    public void Dispose()
    {
        if (System.IO.Directory.Exists(this.root))
        {
            foreach (string f in System.IO.Directory.GetFiles(this.root, "*", System.IO.SearchOption.AllDirectories))
            {
                System.IO.File.SetAttributes(f, System.IO.FileAttributes.Normal);
            }
            System.IO.Directory.Delete(this.root, recursive: true);
        }
    }

    private sealed class ScriptedSender : IMailSender
    {
        private readonly Queue<SendResult> script;

        public ScriptedSender(params SendResult[] results) => this.script = new Queue<SendResult>(results);

        public List<byte[]> Transmitted { get; } = new();

        public Task<SendResult> SendAsync(OutboundDelivery delivery, CancellationToken ct = default)
        {
            this.Transmitted.Add(delivery.RawBytes);
            return Task.FromResult(this.script.Dequeue());
        }
    }

    private sealed class OneKey : IDkimKeyResolver
    {
        private readonly DkimKey key;

        public OneKey(DkimKey key) => this.key = key;

        public Task<DkimKey?> ResolveAsync(string senderDomain, CancellationToken ct = default) => Task.FromResult<DkimKey?>(this.key);
    }

    private (OutboundWorker Worker, EvidenceRecorder Recorder) Build(ScriptedSender sender, string evidenceRoot)
    {
        using RSA rsa = RSA.Create(2048);
        var key = new DkimKey { Domain = "anjal.co.in", Selector = "default", PrivateKeyPem = rsa.ExportPkcs8PrivateKeyPem() };
        var recorder = new EvidenceRecorder(this.store, new EvidenceVault(evidenceRoot));
        var worker = new OutboundWorker(this.store, sender, new OutboundWorkerOptions(), () => this.now, log: null,
            dkimResolver: new OneKey(key), dkimSigner: new DkimSigner(clock: () => this.now), requireDkim: true, evidence: recorder);
        return (worker, recorder);
    }

    private async Task<OutboundMessage> QueueAsync() => await this.store.EnqueueOutboundAsync(new OutboundMessage
    {
        EnvelopeFrom = "arun@anjal.co.in",
        EnvelopeTo = "doctor@example.org",
        RawBytes = Encoding.ASCII.GetBytes(Message),
        CreatedAt = this.now,
        NextAttemptAt = this.now,
        GiveUpAt = this.now.AddHours(24),
    });

    [Fact]
    public async Task OutgoingMail_IsSignedOnce_KeptAsTransmitted_AndEveryAttemptIsRecorded()
    {
        var sender = new ScriptedSender(
            new SendResult { Outcome = SendOutcome.TransientFailure, ReplyCode = 451, Message = "4.7.1 Try again later", RemoteHost = "mx1.example.org", TransportTls = "TLSv1.3 TLS_AES_256_GCM_SHA384" },
            new SendResult { Outcome = SendOutcome.Sent, ReplyCode = 250, Message = "2.0.0 OK queued as 7F3A", RemoteHost = "mx1.example.org", TransportTls = "TLSv1.3 TLS_AES_256_GCM_SHA384" });
        (OutboundWorker worker, _) = this.Build(sender, this.root);
        OutboundMessage queued = await this.QueueAsync();

        await worker.DrainOnceAsync();
        this.now = this.now.AddMinutes(20);   // the signer's clock moves: re-signing would change the bytes
        await worker.DrainOnceAsync();

        Assert.Equal(2, sender.Transmitted.Count);
        Assert.Equal(sender.Transmitted[0], sender.Transmitted[1]);                  // the retry sent the identical signed bytes
        Assert.StartsWith("DKIM-Signature:", Encoding.ASCII.GetString(sender.Transmitted[0]), System.StringComparison.Ordinal);

        (System.Guid? evidenceId, _) = await this.store.GetOutboundEvidenceLinkAsync(queued.Id);
        EvidenceRow evidence = (await this.store.GetEvidenceAsync(evidenceId!.Value))!;
        byte[] kept = await System.IO.File.ReadAllBytesAsync(System.IO.Path.Combine(this.root, evidence.Path));
        Assert.Equal(sender.Transmitted[0], kept);                                    // the evidence is what was transmitted
        Assert.Equal(EvidenceVault.Sha256Hex(kept), evidence.Sha256);
        Assert.Equal(EvidenceRow.Out, evidence.Direction);
        Assert.Equal("sent", evidence.Outcome);

        IReadOnlyList<EvidenceAttemptRow> attempts = await this.store.ListEvidenceAttemptsAsync(evidence.Id);
        Assert.Equal(2, attempts.Count);
        Assert.Equal(("deferred", 451, "4.7.1 Try again later"), (attempts[0].Outcome, attempts[0].ReplyCode!.Value, attempts[0].ReplyText));
        Assert.Equal(("delivered", 250, "2.0.0 OK queued as 7F3A"), (attempts[1].Outcome, attempts[1].ReplyCode!.Value, attempts[1].ReplyText));
        Assert.All(attempts, a => Assert.Equal(("mx1.example.org", "TLSv1.3 TLS_AES_256_GCM_SHA384", "doctor@example.org"), (a.RemoteHost, a.TransportTls!, a.Recipient)));
    }

    [Fact]
    public async Task WhenTheOutgoingCopyCannotBeKept_NothingIsSent_AndTheMailStaysQueued()
    {
        System.IO.Directory.CreateDirectory(this.root);
        string blocker = System.IO.Path.Combine(this.root, "not-a-directory");
        await System.IO.File.WriteAllTextAsync(blocker, "a file where the evidence folder should be");
        var sender = new ScriptedSender(new SendResult { Outcome = SendOutcome.Sent, ReplyCode = 250 });
        (OutboundWorker worker, _) = this.Build(sender, blocker);
        OutboundMessage queued = await this.QueueAsync();

        await worker.DrainOnceAsync();

        Assert.Empty(sender.Transmitted);
        OutboundMessage after = (await this.store.GetOutboundByIdAsync(queued.Id))!;
        Assert.Equal(OutboundStatus.Pending, after.Status);
        Assert.Contains("could not be kept as evidence", after.LastError, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingTheSentCopy_StartsTheClock_OfItsOutgoingEvidence()
    {
        var sender = new ScriptedSender(new SendResult { Outcome = SendOutcome.Sent, ReplyCode = 250, Message = "2.0.0 OK", RemoteHost = "mx1.example.org" });
        (OutboundWorker worker, _) = this.Build(sender, this.root);
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa" });
        MailboxRow mailbox = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "arun", Domain = "anjal.co.in" });
        FolderRow sentFolder = await this.store.EnsureFolderAsync(mailbox.Id, "Sent");
        MessageRow sentCopy = await this.store.SaveMessageAsync(new MessageRow { MailboxId = mailbox.Id, FolderId = sentFolder.Id, MaildirFile = "s1" });
        OutboundMessage queued = await this.QueueAsync();
        await this.store.LinkOutboundToSentCopyAsync(new[] { queued.Id }, sentCopy.Id);

        await worker.DrainOnceAsync();
        (System.Guid? evidenceId, _) = await this.store.GetOutboundEvidenceLinkAsync(queued.Id);
        Assert.Null((await this.store.GetEvidenceAsync(evidenceId!.Value))!.AllCopiesDeletedAt);

        await this.store.DeleteMessageAsync(sentCopy.Id);
        EvidenceRow evidence = (await this.store.GetEvidenceAsync(evidenceId.Value))!;
        Assert.NotNull(evidence.AllCopiesDeletedAt);
        Assert.Equal(1095, (evidence.PurgeAfter!.Value - evidence.AllCopiesDeletedAt!.Value).TotalDays);
    }
}
