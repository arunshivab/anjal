using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

/// <summary>
/// D-88 (owner, 10 Oct 2026): backup.sh tries a failed run again for about 30 minutes, then adds a
/// "failed" line to its record. The "Last good backup" tile turns red at once - so the operators
/// are mailed within 15 minutes - and the next good run clears it.
/// </summary>
public sealed class D88BackupTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AGoodRun_IsGreen_AndTheOldOneLineRecordStillReads()
    {
        HealthTile tile = MailboxService.BackupTile("ok 2026-10-10T03:00:00Z\n", Now);
        Assert.Equal("ok", tile.Level);
        Assert.Equal("{n} h ago, verified", tile.Text);
        Assert.Equal("3", tile.Args!["n"]);
    }

    [Fact]
    public void AFailedRun_TurnsItRedAtOnce_ThoughTheLastGoodOneIsRecent()
    {
        HealthTile tile = MailboxService.BackupTile("ok 2026-10-09T21:05:00Z\nfailed 2026-10-10T05:40:00Z after 5 tries (exit 1)\n", Now);
        Assert.Equal("bad", tile.Level);
        Assert.Equal("Failed {date} after 30 minutes of tries; see the backup log", tile.Text);
        Assert.Contains("2026", tile.Args!["date"], StringComparison.Ordinal);
    }

    [Fact]
    public void AFailure_WithNoGoodBackupYet_IsAFailure()
    {
        (DateTimeOffset? good, DateTimeOffset? failed) = MailboxService.ReadBackupStatus("failed 2026-10-10T05:40:00Z after 1 tries (exit 3)");
        Assert.Null(good);
        Assert.NotNull(failed);
        Assert.Equal("bad", MailboxService.BackupTile("failed 2026-10-10T05:40:00Z after 1 tries (exit 3)", Now).Level);
    }

    [Fact]
    public void AGoodRunAfterTheFailure_ClearsIt()
    {
        (DateTimeOffset? good, DateTimeOffset? failed) = MailboxService.ReadBackupStatus("failed 2026-10-09T21:40:00Z after 5 tries (exit 1)\nok 2026-10-10T05:00:00Z");
        Assert.NotNull(good);
        Assert.Null(failed);
        Assert.Equal("ok", MailboxService.BackupTile("ok 2026-10-10T05:00:00Z", Now).Level);
    }

    [Fact]
    public void NoRecord_IsRed()
    {
        Assert.Equal("No verified backup recorded", MailboxService.BackupTile(string.Empty, Now).Text);
    }

    [Fact]
    public void TheScript_TriesAgainFor30Minutes_AndRecordsTheFailure()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "backup.sh"));
        Assert.Contains("120 240 480 960", script, StringComparison.Ordinal);
        Assert.Contains("printf 'failed %s after %s tries", script, StringComparison.Ordinal);
        Assert.Contains("exit 3", script, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
