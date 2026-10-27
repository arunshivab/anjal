using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Mailbox.Tests;

/// <summary>
/// rc.25 (DES-15 §13): organisation-held encryption - what the database and the disks hold after
/// mail arrives, each way back to a person's mail (their password, an administrator's restore, the
/// recovery sheet, the break-glass key), sessions, the outgoing queue, and large files.
/// </summary>
public sealed class Rc25EncryptionTests : IDisposable
{
    private const string Password = "Lotus garden morning";
    private static readonly string[] Arun = { "arun@sgh.in" };
    private static readonly string[] SecretWords = { "Ramesh", "biopsy", "MRN-1042", "malignant" };
    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc25-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly MailKeyManager keys;

    public Rc25EncryptionTests()
    {
        this.keys = new MailKeyManager(this.store, this.store, RandomNumberGenerator.GetBytes(32));
    }

    public void Dispose()
    {
        // Evidence files are read-only; a background writer may still be moving a file as the test ends.
        TestFolders.DeleteReadOnly(this.root);
    }

    [Fact]
    public async Task AnjalCannotReadIt_TheRowsTheFilesAndTheEvidenceHoldNoWordOfTheMail_YetThePasswordOpensIt()
    {
        (TenantRow tenant, MailboxRow arun) = await this.OrganisationAsync();
        Assert.NotNull(await this.keys.CreateMailboxKeysAsync(arun, Password));
        var maildir = new MaildirStore(Path.Combine(this.root, "mail"), "test") { Keys = new MailboxKeyDirectory(this.store, this.store) };
        var sink = new MailboxSink(this.store, maildir, _ => { });
        var evidence = new EvidenceRecorder(this.store, new EvidenceVault(Path.Combine(this.root, "evidence"))) { Keys = new EvidenceKeyDirectory(this.store, this.store) };
        const string Message = "From: lab@example.org\r\nTo: arun@sgh.in\r\nSubject: Biopsy Ramesh MRN-1042\r\nMessage-ID: <b1@example.org>\r\n\r\nThe biopsy shows the growth is malignant.\r\n";
        await DeliverThroughSmtpAsync(sink, evidence, Message);

        MessageRow raw = Assert.Single(this.store.MailboxMessages);
        foreach (string word in SecretWords)
        {
            Assert.DoesNotContain(word, raw.Subject, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(word, raw.BodyText, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("lab@example.org", raw.FromHeader, StringComparison.Ordinal);   // From and To stay visible (D-231)
        foreach (string file in Directory.GetFiles(this.root, "*", SearchOption.AllDirectories))
        {
            string bytes = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(file));
            foreach (string word in SecretWords)
            {
                Assert.DoesNotContain(word, bytes, StringComparison.OrdinalIgnoreCase);
            }
        }

        UnlockResult opened = await this.keys.UnlockAsync(arun, Password);
        Assert.Equal(UnlockOutcome.Opened, opened.Outcome);
        using var ring = new MailKeyring();
        ring.Add(arun.Id, opened.Keys!.PrivateKey, opened.Keys.DataKey);
        Assert.Equal("Biopsy Ramesh MRN-1042", MailEnvelope.OpenField(raw.Subject, ring));
        using (MailKeyring.Use(ring))
        {
            FolderRow inbox = (await this.store.ListFoldersAsync(arun.Id)).Single(f => f.Name == FolderRow.Inbox);
            byte[]? file = await maildir.ReadAsync(tenant.Slug, arun.Address, inbox.Name, raw.MaildirFile);
            Assert.Contains("malignant", Encoding.UTF8.GetString(file!), StringComparison.Ordinal);
        }
        // Without the keys the file is not read as anything.
        FolderRow box = (await this.store.ListFoldersAsync(arun.Id)).Single(f => f.Name == FolderRow.Inbox);
        await Assert.ThrowsAsync<MailLockedException>(() => maildir.ReadAsync(tenant.Slug, arun.Address, box.Name, raw.MaildirFile));
    }

    [Fact]
    public async Task AForgottenPassword_LocksTheMail_UntilAnAdministratorRestoresIt_ThenTheNewPasswordOpensIt()
    {
        (TenantRow tenant, MailboxRow arun) = await this.OrganisationAsync();
        UnlockedKeys made = (await this.keys.CreateMailboxKeysAsync(arun, Password))!;
        Assert.True(await this.keys.StartRestoreAsync(arun.Id, "New password, set by the recovery address"));
        Assert.Equal(UnlockOutcome.Locked, (await this.keys.UnlockAsync(arun, Password)).Outcome);
        Assert.Equal(UnlockOutcome.Locked, (await this.keys.UnlockAsync(arun, "New password, set by the recovery address")).Outcome);
        Assert.Single(await this.store.ListWaitingRestoresAsync(tenant.Id));

        byte[] recovery = (await this.keys.OpenRecoveryAsync(tenant.Id, Guid.Empty, made))!;   // the temporary copy, before a sheet
        Assert.Null(await this.keys.GrantRestoreAsync(arun.Id, recovery, "admin@sgh.in"));
        UnlockResult restored = await this.keys.UnlockAsync(arun, "New password, set by the recovery address");
        Assert.Equal(UnlockOutcome.Restored, restored.Outcome);
        Assert.Equal(made.DataKey, restored.Keys!.DataKey);
        Assert.Null(await this.store.GetRestoreRequestAsync(arun.Id));
        Assert.Equal(UnlockOutcome.Opened, (await this.keys.UnlockAsync(arun, "New password, set by the recovery address")).Outcome);
    }

    [Fact]
    public async Task TheRecoverySheet_IsTypedBack_TheTemporaryCopyGoes_AndTheSheetAloneOpensTheRecoveryKey()
    {
        (TenantRow tenant, MailboxRow arun) = await this.OrganisationAsync();
        UnlockedKeys made = (await this.keys.CreateMailboxKeysAsync(arun, Password))!;
        byte[] recovery = (await this.keys.OpenRecoveryAsync(tenant.Id, Guid.Empty, made))!;
        Assert.True(await this.keys.GrantAdminAsync(tenant.Id, arun.Id, recovery));
        string sheet = PaperForm.Write(await this.keys.IssueSheetAsync(tenant.Id, recovery));

        string typo = sheet[..5] + (sheet[5] == 'A' ? 'B' : 'A') + sheet[6..];
        Assert.False(await this.keys.ConfirmSheetAsync(tenant.Id, typo));
        Assert.NotNull((await this.store.GetTenantKeysAsync(tenant.Id))!.RecoveryByServer);
        Assert.True(await this.keys.ConfirmSheetAsync(tenant.Id, sheet.ToLowerInvariant()));
        TenantKeyRow row = (await this.store.GetTenantKeysAsync(tenant.Id))!;
        Assert.Null(row.RecoveryByServer);
        Assert.NotNull(row.SheetConfirmedAt);

        // No administrator left: the sheet alone opens the recovery key, which opens the person's key.
        byte[] fromSheet = (await this.keys.OpenWithSheetAsync(tenant.Id, sheet))!;
        Assert.Equal(recovery, fromSheet);
        Assert.Equal(made.PrivateKey, await this.keys.OpenMailboxPrivateByOrgAsync(arun.Id, fromSheet));
        Assert.Null(await this.keys.OpenWithSheetAsync(tenant.Id, typo));
        // The administrator's own copy still opens it, through their mailbox key.
        Assert.Equal(recovery, await this.keys.OpenRecoveryAsync(tenant.Id, arun.Id, made));
    }

    [Fact]
    public async Task TheBreakGlassKey_AnyTwoSharesOpenTheSheet_OneAloneCannot_AndAnOrganisationThatSwitchedItOffIsNotReached()
    {
        (TenantRow tenant, MailboxRow arun) = await this.OrganisationAsync();
        UnlockedKeys made = (await this.keys.CreateMailboxKeysAsync(arun, Password))!;
        byte[] recovery = (await this.keys.OpenRecoveryAsync(tenant.Id, Guid.Empty, made))!;

        // What "Anjal.BreakGlass make" does, offline.
        (byte[] pub, byte[] priv) = MailKeys.NewKeyPair();
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        byte[] wrapped = MailKeys.Wrap(priv, MailKeys.RecoveryWrapKey(secret));
        IReadOnlyList<byte[]> shares = SecretShares.Split(secret);
        string line = BreakGlassLine.Write(pub, wrapped);
        await this.store.SaveBreakGlassAsync(new BreakGlassRow(BreakGlassLine.Read(line)!.Value.PublicKey, wrapped, DateTimeOffset.UtcNow));

        byte[] sheetSecret = await this.keys.IssueSheetAsync(tenant.Id, recovery);
        byte[] sealedSecret = (await this.store.GetTenantKeysAsync(tenant.Id))!.SecretByBreakGlass!;
        foreach ((int a, int b) in new[] { (0, 1), (0, 2), (1, 2), (2, 0) })
        {
            byte[] rebuilt = SecretShares.Combine(PaperForm.Read(PaperForm.Write(shares[a]))!, shares[b]);
            using var ring = new MailKeyring();
            ring.AddPrivateKey(MailKeys.Unwrap(wrapped, MailKeys.RecoveryWrapKey(rebuilt))!);
            Assert.Equal(sheetSecret, MailEnvelope.Open(sealedSecret, ring));
        }
        Assert.Throws<ArgumentException>(() => SecretShares.Combine(shares[1], shares[1]));
        Assert.NotEqual(secret, shares[0][1..]);
        Assert.Null(MailKeys.Unwrap(wrapped, MailKeys.RecoveryWrapKey(shares[0][1..])));

        // The organisation switches it off: the next sheet is sealed to nothing.
        await this.keys.SetBreakGlassAsync(tenant.Id, on: false);
        Assert.Null((await this.store.GetTenantKeysAsync(tenant.Id))!.SecretByBreakGlass);
        await this.keys.IssueSheetAsync(tenant.Id, recovery);
        Assert.Null((await this.store.GetTenantKeysAsync(tenant.Id))!.SecretByBreakGlass);
    }

    [Fact]
    public async Task ASession_KeepsTheKeysOnlyForTheBrowserThatHoldsTheCookie_AndSigningOutLeavesNothingUsable()
    {
        (TenantRow _, MailboxRow arun) = await this.OrganisationAsync();
        UnlockedKeys made = (await this.keys.CreateMailboxKeysAsync(arun, Password))!;
        byte[] token = MailKeyManager.NewSessionToken();
        await this.keys.SaveSessionAsync("s1", arun.Id, token, made, DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(made.DataKey, (await this.keys.LoadSessionAsync("s1", arun.Id, token))!.DataKey);
        Assert.Null(await this.keys.LoadSessionAsync("s1", arun.Id, MailKeyManager.NewSessionToken()));
        Assert.Null(await this.keys.LoadSessionAsync("s2", arun.Id, token));
        await this.store.DeleteSessionKeysAsync("s1");
        Assert.Null(await this.keys.LoadSessionAsync("s1", arun.Id, token));
        await this.keys.SaveSessionAsync("s3", arun.Id, token, made, DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(1, await this.store.PurgeSessionKeysAsync(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task AChangedPepper_LocksTheMail_AndStartsARestore_AndTheRightPepperOpensItAgain()
    {
        (TenantRow tenant, MailboxRow arun) = await this.OrganisationAsync();
        UnlockedKeys made = (await this.keys.CreateMailboxKeysAsync(arun, Password))!;
        var otherPepper = new MailKeyManager(this.store, this.store, RandomNumberGenerator.GetBytes(32));
        Assert.Equal(UnlockOutcome.Locked, (await otherPepper.UnlockAsync(arun, Password)).Outcome);
        Assert.Single(await this.store.ListWaitingRestoresAsync(tenant.Id));
        // A misconfigured pepper put right: the password's own copy was kept, and opens the mail.
        UnlockResult back = await this.keys.UnlockAsync(arun, Password);
        Assert.Equal(UnlockOutcome.Opened, back.Outcome);
        Assert.Equal(made.DataKey, back.Keys!.DataKey);
        Assert.Empty(await this.store.ListWaitingRestoresAsync(tenant.Id));
    }

    [Fact]
    public async Task TwoFirstSignInsAtOnce_MakeOneSetOfKeys_AndBothOpenIt()
    {
        (TenantRow tenant, MailboxRow arun) = await this.OrganisationAsync();
        MailboxRow meera = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "meera", Domain = "sgh.in" });
        UnlockResult[] both = await Task.WhenAll(
            Task.Run(() => this.keys.UnlockAsync(arun, Password)),
            Task.Run(() => this.keys.UnlockAsync(arun, Password)),
            Task.Run(() => this.keys.UnlockAsync(meera, Password)));
        Assert.All(both, r => Assert.NotNull(r.Keys));
        Assert.Equal(both[0].Keys!.DataKey, both[1].Keys!.DataKey);
        MailboxKeyRow stored = (await this.store.GetMailboxKeysAsync(arun.Id))!;
        Assert.Equal(MailKeys.PublicOf(both[0].Keys!.PrivateKey), stored.PublicKey);
        // Every mailbox key is sealed to the one recovery key the organisation kept.
        byte[] recovery = (await this.keys.OpenRecoveryAsync(tenant.Id, Guid.Empty, both[0].Keys!))!;
        Assert.NotNull(await this.keys.OpenMailboxPrivateByOrgAsync(arun.Id, recovery));
        Assert.NotNull(await this.keys.OpenMailboxPrivateByOrgAsync(meera.Id, recovery));
        // A key, once stored, never changes.
        MailboxKeyRow other = new() { MailboxId = arun.Id, PublicKey = MailKeys.NewKeyPair().Public, UnlockSalt = stored.UnlockSalt, PrivateByOrg = stored.PrivateByOrg, DataKey = stored.DataKey };
        await Assert.ThrowsAsync<InvalidOperationException>(() => this.store.SaveMailboxKeysAsync(other));
    }

    [Fact]
    public void ATextThatOnlyLooksSealed_IsReadAsText_AndNeverStopsTheReKeying()
    {
        using var ring = new MailKeyring();
        Assert.False(MailEnvelope.IsField("e1. Final report"));
        Assert.Equal("e1. Final report", MailEnvelope.OpenField("e1. Final report", ring));
        const string Fake = "e1.QU5KRQEBnot-an-envelope";
        Assert.Equal(Fake, MailEnvelope.OpenField(Fake, ring));
        Assert.Equal(Fake, MailEnvelope.RekeyField(Fake, ring, MailKeys.NewSymmetricKey()));
        string looksWhole = MailEnvelope.FieldPrefix + MailEnvelope.Base64Url(Encoding.ASCII.GetBytes("ANJE").Concat(new byte[] { 1, 1 }).Concat(new byte[60]).ToArray());
        Assert.Equal(looksWhole, MailEnvelope.RekeyField(looksWhole, ring, MailKeys.NewSymmetricKey()));
        Assert.Empty(OutboundCipher.ForReader(Encoding.ASCII.GetBytes("ANJQ").Concat(new byte[] { 1, 0xFF, 0xFF, 0xFF, 0xFF }).ToArray()));
        Assert.Null(OutboundCipher.RecordOnly(Encoding.ASCII.GetBytes("ANJQ").Concat(new byte[] { 1, 0x7F, 0xFF, 0xFF, 0xFF }).ToArray()));
    }

    [Fact]
    public void TheOutgoingQueue_ShowsOnlyTheRecordOutsideTheMailServer_AndKeepsOnlyTheRecordOnceDelivered()
    {
        (byte[] sealPub, byte[] sealPriv) = MailKeys.NewKeyPair();
        (byte[] recordPub, byte[] recordPriv) = MailKeys.NewKeyPair();
        byte[] raw = Encoding.UTF8.GetBytes("From: arun@sgh.in\r\nTo: dr.rao@example.org\r\nSubject: Ramesh biopsy\r\n\r\nThe biopsy is malignant.\r\n");
        byte[] queued = OutboundCipher.Seal(raw, sealPub, recordPub);
        Assert.DoesNotContain("malignant", Encoding.Latin1.GetString(queued), StringComparison.Ordinal);
        Assert.DoesNotContain("Ramesh", Encoding.Latin1.GetString(queued), StringComparison.Ordinal);
        Func<byte[], byte[]?>? before = OutboundCipher.ServerOpener;
        try
        {
            OutboundCipher.ServerOpener = null;
            using (var reader = new MailKeyring())
            {
                reader.AddPrivateKey(recordPriv);
                using (MailKeyring.Use(reader))
                {
                    string seen = Encoding.UTF8.GetString(OutboundCipher.ForReader(queued));
                    Assert.Contains("Subject: Ramesh biopsy", seen, StringComparison.Ordinal);
                    Assert.DoesNotContain("malignant", seen, StringComparison.Ordinal);
                }
            }
            Assert.Empty(OutboundCipher.ForReader(queued));   // nobody's keys: nothing
            using (var server = new MailKeyring())
            {
                server.AddPrivateKey(sealPriv);
                OutboundCipher.ServerOpener = e => MailEnvelope.Open(e, server);
                Assert.Equal(raw, OutboundCipher.ForReader(queued));
                byte[] kept = OutboundCipher.RecordOnly(queued)!;
                Assert.True(kept.Length < queued.Length);
                Assert.DoesNotContain("malignant", Encoding.UTF8.GetString(OutboundCipher.ForReader(kept)), StringComparison.Ordinal);
            }
        }
        finally
        {
            OutboundCipher.ServerOpener = before;
        }
    }

    [Fact]
    public async Task ALargeFile_IsWrittenAndReadInPieces_AndAFileCutShortIsRefused()
    {
        byte[] key = MailKeys.NewSymmetricKey();
        byte[] plain = RandomNumberGenerator.GetBytes((SealedFile.PieceSize * 2) + 1234);
        using var target = new MemoryStream();
        using (Stream w = SealedFile.Writer(target, key))
        {
            w.Write(plain, 0, 100);
            w.Write(plain, 100, plain.Length - 100);
        }
        byte[] sealedBytes = target.ToArray();
        Assert.True(SealedFile.IsSealed(sealedBytes));
        using var ring = new MailKeyring();
        ring.AddSymmetricKey(key);
        using var back = new MemoryStream();
        await SealedFile.OpenToAsync(new MemoryStream(sealedBytes), back, ring);
        Assert.Equal(plain, back.ToArray());
        await Assert.ThrowsAnyAsync<EndOfStreamException>(() => SealedFile.OpenToAsync(new MemoryStream(sealedBytes[..^4]), Stream.Null, ring));
        using var wrong = new MailKeyring();
        wrong.AddSymmetricKey(MailKeys.NewSymmetricKey());
        await Assert.ThrowsAsync<InvalidDataException>(() => SealedFile.OpenToAsync(new MemoryStream(sealedBytes), Stream.Null, wrong));
    }

    [Fact]
    public async Task EncryptedContacts_StillAnswerArrival_ThroughTheirAddressMarks()
    {
        (TenantRow _, MailboxRow arun) = await this.OrganisationAsync();
        UnlockedKeys made = (await this.keys.CreateMailboxKeysAsync(arun, Password))!;
        byte[] pepper = RandomNumberGenerator.GetBytes(32);
        await this.store.SetMailboxDocumentAsync(arun.Id, "contacts", MailEnvelope.FieldWithKey("[{\"Address\":\"meera@example.org\"}]", made.DataKey));
        await this.store.SetMailboxDocumentAsync(arun.Id, MailboxSink.ContactMarksKind, "[\"" + MailKeys.AddressMark(arun.Id, "Meera@Example.org", pepper) + "\"]");
        await AwayReply.WriteAsync(this.store, arun.Id, new AwayReply { On = true, Subject = "Away", Text = "Back Monday.", ReplyTo = "contacts" });
        var maildir = new MaildirStore(Path.Combine(this.root, "mail"), "test") { Keys = new MailboxKeyDirectory(this.store, this.store) };
        var sink = new MailboxSink(this.store, maildir, _ => { }) { AddressPepper = pepper };
        foreach (string from in new[] { "meera@example.org", "stranger@example.org" })
        {
            await sink.DeliverAsync(new DeliveryContext
            {
                EnvelopeFrom = from,
                EnvelopeTo = Arun,
                RawBytes = Encoding.UTF8.GetBytes($"From: <{from}>\r\nTo: <arun@sgh.in>\r\nSubject: Hello\r\nMessage-ID: <{Guid.NewGuid():N}@example.org>\r\n\r\nHi.\r\n"),
            });
        }
        IReadOnlyList<OutboundMessage> replies = await this.store.ListOutboundForSenderAsync(string.Empty);
        Assert.Contains(replies, m => m.EnvelopeTo == "meera@example.org");
        Assert.DoesNotContain(replies, m => m.EnvelopeTo == "stranger@example.org");
    }

    private static async Task DeliverThroughSmtpAsync(MailboxSink sink, EvidenceRecorder evidence, string message)
    {
        var options = new SmtpServerOptions { BindAddress = IPAddress.Loopback, Port = 0, AdvertisedHostName = "mail.sgh.in", Evidence = evidence };
        using var server = new SmtpServer(options, sink);
        _ = server.StartAsync(CancellationToken.None);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.BoundPort);
        using NetworkStream net = tcp.GetStream();
        using var r = new StreamReader(net, Encoding.ASCII);
        async Task<string> Reply()
        {
            string l;
            do
            {
                l = (await r.ReadLineAsync())!;
            }
            while (l.Length > 3 && l[3] == '-');
            return l;
        }
        await Reply();
        foreach (string c in new[] { "EHLO mx.example.org", "MAIL FROM:<lab@example.org>", "RCPT TO:<arun@sgh.in>", "DATA" })
        {
            await net.WriteAsync(Encoding.ASCII.GetBytes(c + "\r\n"));
            await Reply();
        }
        await net.WriteAsync(Encoding.ASCII.GetBytes(message + ".\r\n"));
        Assert.StartsWith("250", await Reply(), StringComparison.Ordinal);
    }

    private async Task<(TenantRow Tenant, MailboxRow Arun)> OrganisationAsync()
    {
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "sgh" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "sgh.in" });
        MailboxRow arun = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "arun", Domain = "sgh.in", DisplayName = "Arun" });
        return (tenant, arun);
    }
}
