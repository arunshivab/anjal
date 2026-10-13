using Anjal.Smtp;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

/// <summary>
/// rc.15.1 (owner, 12 Oct 2026): on a new server the first leaked-password download takes about
/// an hour. It is expected work, not a fault: while new passwords are also checked online it
/// shows as fine, so the operators get no alert and no "resolved" mail. Only when nothing else
/// covers the gap (download-only mode) is it a warning.
/// </summary>
public sealed class Rc151LeakedTileTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Downloading_WhileCheckedOnline_IsFine()
    {
        HealthTile tile = MailboxService.LeakedPasswordsTile(Now, "both", null, new PwnedRefreshStatus { State = "building", Percent = 40 });
        Assert.Equal("ok", tile.Level);
        Assert.Equal("Downloading: {n}% done", tile.Text);
    }

    [Fact]
    public void NotDownloadedYet_WhileCheckedOnline_IsFine_AndSaysSo()
    {
        HealthTile tile = MailboxService.LeakedPasswordsTile(Now, "both", null, null);
        Assert.Equal("ok", tile.Level);
        Assert.Equal("Not downloaded yet; checked online meanwhile", tile.Text);
    }

    [Fact]
    public void DownloadOnly_WithNoListYet_IsStillAWarning()
    {
        Assert.Equal("warn", MailboxService.LeakedPasswordsTile(Now, "download", null, null).Level);
        Assert.Equal("warn", MailboxService.LeakedPasswordsTile(Now, "download", null, new PwnedRefreshStatus { State = "building", Percent = 10 }).Level);
    }

    [Fact]
    public void AFailedDownload_IsAWarning_WhenCheckedOnline_AndBad_WhenNot()
    {
        PwnedRefreshStatus failed = new() { State = "failed" };
        Assert.Equal("warn", MailboxService.LeakedPasswordsTile(Now, "both", null, failed).Level);
        Assert.Equal("bad", MailboxService.LeakedPasswordsTile(Now, "download", null, failed).Level);
    }

    [Fact]
    public void OnlineOnly_AndOff_AreUnchanged()
    {
        Assert.Equal("ok", MailboxService.LeakedPasswordsTile(Now, "online", null, null).Level);
        Assert.Equal("warn", MailboxService.LeakedPasswordsTile(Now, "off", null, null).Level);
    }
}
