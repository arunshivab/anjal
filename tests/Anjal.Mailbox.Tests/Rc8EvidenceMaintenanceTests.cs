using System.Text;
using Anjal.Store;

namespace Anjal.Mailbox.Tests;

/// <summary>
/// v1.0.0-rc.8, SPEC-08 R-02 and R-12: label recovery from this server's own
/// Received lines, and reconstruction of evidence for mail stored before rc.8.
/// Dry runs change nothing; applying changes exactly what the dry run reported;
/// a second run finds nothing to do; message files are never changed.
/// </summary>
public sealed class Rc8EvidenceMaintenanceTests : System.IDisposable
{
    private const string Host = "mail.anjal.co.in";

    private const string Original =
        "Received: from mta.yahoo.com (mta.yahoo.com [66.163.185.1]) by relay.yahoo.com; Sun, 27 Sep 2026 18:22:00 +0000\r\n" +
        "Authentication-Results: mail.anjal.co.in; spf=pass (a sender's own line, further down - it stays)\r\n" +
        "From: Shiva <arunshiva_b@yahoo.com>\r\nTo: arun@anjal.co.in\r\nSubject: Re: testing firewall\r\n\r\nThanks.\r\n";

    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-rc8-maint-" + System.Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();

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

    private static string Ours(string protocol, string? tls) =>
        $"Received: from sonic.mail.yahoo.com ([66.163.185.1])\r\n\tby {Host} with {protocol} id 0123456789abcdef{(tls is null ? string.Empty : " (" + tls + ")")};\r\n\tSun, 27 Sep 2026 18:22:40 +0000\r\n" +
        "X-Anjal-Spam-Score: 0\r\nX-Anjal-Spam-Reasons: none\r\n" +
        $"Authentication-Results: {Host}; spf=pass smtp.mailfrom=yahoo.com; dkim=pass; dmarc=pass\r\n";

    [Theory]
    [InlineData("ESMTPS", "TLSv1.3 TLS_AES_128_GCM_SHA256", "ESMTPS", "TLSv1.3 TLS_AES_128_GCM_SHA256")]
    [InlineData("ESMTPS", null, "ESMTPS", null)]
    [InlineData("ESMTP", null, "ESMTP", null)]
    [InlineData("ESMTPSA", "TLSv1.3 TLS_AES_256_GCM_SHA384", "ESMTPSA", "TLSv1.3 TLS_AES_256_GCM_SHA384")]
    public void OwnReceivedLine_IsRead_InEveryFormThisServerHasWritten(string protocol, string? tls, string expectedProtocol, string? expectedTls)
    {
        byte[] raw = Encoding.ASCII.GetBytes(Ours(protocol, tls) + Original);
        (string Protocol, string? Tls)? read = EvidenceMaintenance.ReadOwnReceived(raw, Host);
        Assert.Equal((expectedProtocol, expectedTls), read!.Value);
    }

    [Fact]
    public void AReceivedLineNamingAnotherServer_IsNotReadAsOurs()
    {
        Assert.Null(EvidenceMaintenance.ReadOwnReceived(Encoding.ASCII.GetBytes(Original), Host));
    }

    [Fact]
    public void RemovingOurAdditions_GivesBackTheOriginalExactly_AndStopsAtTheFirstLineWeDidNotAdd()
    {
        byte[] stored = Encoding.ASCII.GetBytes(Ours("ESMTPS", "TLSv1.3 TLS_AES_128_GCM_SHA256") + Original);
        Assert.Equal(Encoding.ASCII.GetBytes(Original), EvidenceMaintenance.RemoveOwnAdditions(stored, Host));
        Assert.Equal(Encoding.ASCII.GetBytes(Original), EvidenceMaintenance.RemoveOwnAdditions(Encoding.ASCII.GetBytes(Original), Host));
    }

    private async Task<(MessageRow Row, string StoredPath)> StoreAsync(string raw, bool? encrypted, string? tls)
    {
        var maildir = new MaildirStore(System.IO.Path.Combine(this.root, "mail"), "test");
        TenantRow tenant = (await this.store.GetTenantAsync("imagiqa")) ?? await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa" });
        MailboxRow mailbox = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "arun", Domain = "anjal.co.in" });
        FolderRow inbox = await this.store.EnsureFolderAsync(mailbox.Id, FolderRow.Inbox);
        MaildirWriteResult written = await maildir.WriteAsync("imagiqa", mailbox.Address, inbox.Name, Encoding.ASCII.GetBytes(raw));
        MessageRow row = await this.store.SaveMessageAsync(new MessageRow { MailboxId = mailbox.Id, FolderId = inbox.Id, MaildirFile = written.RelativePath, Subject = "s", TransportEncrypted = encrypted, TransportTls = tls });
        return (row, written.FullPath);
    }

    private EvidenceMaintenance Maintenance() => new(this.store, this.store, new MaildirStore(System.IO.Path.Combine(this.root, "mail"), "test"), new EvidenceVault(System.IO.Path.Combine(this.root, "evidence")), Host);

    [Fact]
    public async Task LabelRecovery_DryRunChangesNothing_ApplyFixesWhatItReported_AndASecondRunFindsNothing()
    {
        // DEF-065: encrypted mail recorded as unencrypted.
        (MessageRow yahoo, string yahooFile) = await this.StoreAsync(Ours("ESMTPS", "TLSv1.3 TLS_AES_128_GCM_SHA256") + Original, false, null);
        (MessageRow plain, _) = await this.StoreAsync(Ours("ESMTP", null) + Original, null, null);          // before rc.7: nothing recorded
        (MessageRow submitted, _) = await this.StoreAsync(Ours("ESMTPSA", "TLSv1.3 X") + Original, null, null); // a user's submission: stays null
        byte[] fileBefore = await System.IO.File.ReadAllBytesAsync(yahooFile);

        LabelRecoveryReport dry = await this.Maintenance().RecoverTransportLabelsAsync(apply: false);
        Assert.Equal(2, dry.Changes.Count);
        Assert.False((await this.store.GetMessageByIdAsync(yahoo.Id))!.TransportEncrypted);

        LabelRecoveryReport applied = await this.Maintenance().RecoverTransportLabelsAsync(apply: true);
        Assert.Equal(dry.Changes.Select(c => c.MessageId).Order(), applied.Changes.Select(c => c.MessageId).Order());
        MessageRow y = (await this.store.GetMessageByIdAsync(yahoo.Id))!;
        Assert.Equal((true, "TLSv1.3 TLS_AES_128_GCM_SHA256"), (y.TransportEncrypted!.Value, y.TransportTls!));
        Assert.False((await this.store.GetMessageByIdAsync(plain.Id))!.TransportEncrypted);
        Assert.Null((await this.store.GetMessageByIdAsync(submitted.Id))!.TransportEncrypted);
        Assert.Equal(fileBefore, await System.IO.File.ReadAllBytesAsync(yahooFile));

        Assert.Empty((await this.Maintenance().RecoverTransportLabelsAsync(apply: true)).Changes);
    }

    [Fact]
    public async Task Reconstruction_KeepsTheOriginalWithOurLinesRemoved_MarkedReconstructed_OnceOnly()
    {
        (MessageRow row, string file) = await this.StoreAsync(Ours("ESMTPS", "TLSv1.3 TLS_AES_128_GCM_SHA256") + Original, true, "TLSv1.3 TLS_AES_128_GCM_SHA256");
        byte[] fileBefore = await System.IO.File.ReadAllBytesAsync(file);

        ReconstructionReport dry = await this.Maintenance().ReconstructEvidenceAsync(apply: false);
        Assert.Equal((1, 0), (dry.Candidates, dry.Created));
        Assert.Null((await this.store.GetMessageByIdAsync(row.Id))!.EvidenceId);

        ReconstructionReport applied = await this.Maintenance().ReconstructEvidenceAsync(apply: true);
        Assert.Equal(1, applied.Created);
        MessageRow linked = (await this.store.GetMessageByIdAsync(row.Id))!;
        EvidenceRow evidence = (await this.store.GetEvidenceAsync(linked.EvidenceId!.Value))!;
        Assert.True(evidence.Reconstructed);
        Assert.Equal(Encoding.ASCII.GetBytes(Original), await System.IO.File.ReadAllBytesAsync(System.IO.Path.Combine(this.root, "evidence", evidence.Path)));
        Assert.Equal(fileBefore, await System.IO.File.ReadAllBytesAsync(file));

        Assert.Equal(0, (await this.Maintenance().ReconstructEvidenceAsync(apply: true)).Created);
    }
}
