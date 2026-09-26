using Anjal.Store;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

/// <summary>
/// Decision 1B (v1.0.0-rc.5): the webmail says whether a message was checked.
/// Before, a blank score meant either "checked, scored 0" or "never checked",
/// and the user's own sent mail read "Spam score 0".
/// </summary>
public class SpamCheckTests
{
    [Theory]
    [InlineData(true, "Sent", true)]
    [InlineData(false, "INBOX", false)]
    [InlineData(null, "INBOX", true)]
    [InlineData(null, "Junk", true)]
    [InlineData(null, "Receipts", true)]
    [InlineData(null, "Sent", false)]
    [InlineData(null, "Drafts", false)]
    [InlineData(null, "Trash", false)]
    public void WasChecked_UsesTheRecordedValue_ElseTheFolder(bool? recorded, string folder, bool expected)
    {
        Assert.Equal(expected, SpamCheck.WasChecked(new MessageRow { SpamChecked = recorded }, folder));
    }

    [Fact]
    public void NotCheckedNote_SaysWhoseMailItIs()
    {
        Assert.StartsWith("Your own message", SpamCheck.NotCheckedNote("Sent"), System.StringComparison.Ordinal);
        Assert.StartsWith("Your own message", SpamCheck.NotCheckedNote("Drafts"), System.StringComparison.Ordinal);
        Assert.StartsWith("Not checked on arrival", SpamCheck.NotCheckedNote("INBOX"), System.StringComparison.Ordinal);
    }
}
