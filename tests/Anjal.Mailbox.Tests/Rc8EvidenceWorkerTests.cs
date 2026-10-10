using System.Text;
using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Mailbox.Tests;

/// <summary>
/// v1.0.0-rc.8, SPEC-08 R-07 and R-09: the daily manifest chain, verification
/// that detects tampering, retention purges recorded for the backup, orphan
/// detection that never deletes, and passes that are safe to repeat.
/// </summary>
public sealed class Rc8EvidenceWorkerTests : System.IDisposable
{
    private static readonly string[] Recipient = { "arun@anjal.co.in" };
    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-rc8-worker-" + System.Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly List<string> log = new();
    private System.DateTimeOffset now = new(2026, 10, 3, 6, 0, 0, System.TimeSpan.Zero);

    public Rc8EvidenceWorkerTests()
    {
        // DEF-090: the store stamps "purge after" with its own clock. Give it the
        // test's clock from the start, or the result depends on the real date.
        this.store.EvidenceClock = () => this.now;
    }

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

    private EvidenceVault Vault => new(this.root);

    private EvidenceWorker Worker() => new(this.store, this.Vault, () => this.now, this.log.Add);

    private async Task<System.Guid> KeepAsync(System.DateTimeOffset at, string text, bool accepted = true)
    {
        var recorder = new EvidenceRecorder(this.store, this.Vault);
        System.Guid id = await recorder.RecordInboundAsync(new InboundEvidence
        {
            RawBytes = Encoding.ASCII.GetBytes(text),
            EnvelopeFrom = "doctor@example.org",
            EnvelopeTo = Recipient,
            RemoteAddress = "203.0.113.9",
            ClientHostName = "mx.example.org",
            ReceivedAt = at,
        });
        await recorder.CompleteInboundAsync(id, accepted);
        return id;
    }

    private static void MakeWritable(string path)
    {
        if (System.OperatingSystem.IsWindows())
        {
            System.IO.File.SetAttributes(path, System.IO.FileAttributes.Normal);
        }
        else
        {
            System.IO.File.SetUnixFileMode(path, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task DailyManifests_FormAChain_AndVerifyFindsNothingWrong_AndRepeatingWritesNothing()
    {
        await this.KeepAsync(new System.DateTimeOffset(2026, 10, 1, 9, 0, 0, System.TimeSpan.Zero), "one");
        await this.KeepAsync(new System.DateTimeOffset(2026, 10, 2, 9, 0, 0, System.TimeSpan.Zero), "two");

        EvidencePassResult first = await this.Worker().RunOnceAsync();
        Assert.Equal(2, first.ManifestsWritten);   // 1 and 2 October; 3 October is not complete yet
        IReadOnlyList<EvidenceManifestRow> chain = await this.store.ListEvidenceManifestsAsync();
        Assert.Equal(EvidenceWorker.Genesis, chain[0].PreviousSha256);
        Assert.Equal(chain[0].Sha256, chain[1].PreviousSha256);
        Assert.Empty(await this.Worker().VerifyAsync());

        EvidencePassResult again = await this.Worker().RunOnceAsync();
        Assert.Equal(0, again.ManifestsWritten);
        if (!System.OperatingSystem.IsWindows())
        {
            Assert.Equal(System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.GroupRead, System.IO.File.GetUnixFileMode(this.Vault.FullPath(chain[0].Path)));
        }
    }

    [Fact]
    public async Task Verify_DetectsAnAlteredEvidenceFile_AnAlteredManifest_AndAMissingManifest()
    {
        System.Guid id = await this.KeepAsync(new System.DateTimeOffset(2026, 10, 1, 9, 0, 0, System.TimeSpan.Zero), "original bytes");
        await this.KeepAsync(new System.DateTimeOffset(2026, 10, 2, 9, 0, 0, System.TimeSpan.Zero), "two");
        await this.Worker().RunOnceAsync();
        IReadOnlyList<EvidenceManifestRow> chain = await this.store.ListEvidenceManifestsAsync();

        string file = this.Vault.FullPath((await this.store.GetEvidenceAsync(id))!.Path);
        MakeWritable(file);
        await System.IO.File.WriteAllTextAsync(file, "altered bytes");
        Assert.Contains(await this.Worker().VerifyAsync(), p => p.Contains($"evidence {id}: file altered", System.StringComparison.Ordinal));

        string manifest = this.Vault.FullPath(chain[1].Path);
        MakeWritable(manifest);
        await System.IO.File.AppendAllTextAsync(manifest, "add " + new string('0', 64) + " 1 inserted x.eml\n");
        Assert.Contains(await this.Worker().VerifyAsync(), p => p.Contains("manifest 2026-10-02: file altered", System.StringComparison.Ordinal));

        MakeWritable(this.Vault.FullPath(chain[0].Path));
        System.IO.File.Delete(this.Vault.FullPath(chain[0].Path));
        Assert.Contains(await this.Worker().VerifyAsync(), p => p.Contains("manifest 2026-10-01: file missing", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task Purge_RemovesNotAcceptedAtOnce_AndKeptMailOnlyAfterItsRetention_RecordingEachForTheBackup()
    {
        System.DateTimeOffset received = new(2026, 10, 3, 1, 0, 0, System.TimeSpan.Zero);
        System.Guid refused = await this.KeepAsync(received, "refused by the mailboxes", accepted: false);
        System.Guid kept = await this.KeepAsync(received, "delivered");
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa" });
        MailboxRow mailbox = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "arun", Domain = "anjal.co.in" });
        FolderRow inbox = await this.store.EnsureFolderAsync(mailbox.Id, FolderRow.Inbox);
        MessageRow copy = await this.store.SaveMessageAsync(new MessageRow { MailboxId = mailbox.Id, FolderId = inbox.Id, MaildirFile = "m", EvidenceId = kept });

        EvidencePassResult pass = await this.Worker().RunOnceAsync();
        Assert.Equal(1, pass.Purged);
        EvidenceRow gone = (await this.store.GetEvidenceAsync(refused))!;
        Assert.Equal("not accepted", gone.PurgeReason);
        Assert.False(this.Vault.Exists(gone.Path));
        string purgeList = await System.IO.File.ReadAllTextAsync(this.Vault.FullPath("purge-lists/2026-10-03.txt"));
        Assert.Contains(gone.Path, purgeList, System.StringComparison.Ordinal);

        // The user deletes the message; the evidence stays 3 years.
        this.store.EvidenceClock = () => this.now;
        await this.store.DeleteMessageAsync(copy.Id);
        this.now = this.now.AddDays(1094);
        Assert.Equal(0, (await this.Worker().RunOnceAsync()).Purged);
        this.now = this.now.AddDays(2);
        Assert.Equal(1, (await this.Worker().RunOnceAsync()).Purged);
        EvidenceRow expired = (await this.store.GetEvidenceAsync(kept))!;
        Assert.Equal("retention ended", expired.PurgeReason);
        Assert.False(this.Vault.Exists(expired.Path));

        // Both purges appear in the manifest of the day they happened.
        string day3 = await System.IO.File.ReadAllTextAsync(this.Vault.FullPath("manifests/2026-10-03.txt"));
        Assert.Contains($"purge {gone.Sha256} ", day3, System.StringComparison.Ordinal);
        Assert.Empty(await this.Worker().VerifyAsync());
    }

    [Fact]
    public async Task AFileWithNoDatabaseRecord_IsReportedAsAnOrphan_AndLeftInPlace()
    {
        await this.KeepAsync(new System.DateTimeOffset(2026, 10, 1, 9, 0, 0, System.TimeSpan.Zero), "one");
        string stray = this.Vault.FullPath("2026/10/01/" + System.Guid.NewGuid().ToString("N") + ".eml");
        await System.IO.File.WriteAllTextAsync(stray, "written just before a crash");

        await this.Worker().RunOnceAsync();

        string manifest = await System.IO.File.ReadAllTextAsync(this.Vault.FullPath("manifests/2026-10-01.txt"));
        Assert.Contains("alert orphan 2026/10/01/", manifest, System.StringComparison.Ordinal);
        Assert.Contains(this.log, l => l.StartsWith("CRIT evidence 2026-10-01: alert orphan", System.StringComparison.Ordinal));
        Assert.True(System.IO.File.Exists(stray));
    }
}
