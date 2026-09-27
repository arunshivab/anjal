using Anjal.Store;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

/// <summary>v1.0.0-rc.7: when the red open lock and the warning are shown.</summary>
public sealed class Rc7UnencryptedWarningTests
{
    private static MessageRow Row(bool? encrypted) => new()
    {
        FromHeader = "Doctor <Doctor@Rediffmail.com>",
        EnvelopeFrom = "bounce@rediffmail.com",
        TransportEncrypted = encrypted,
    };

    [Fact]
    public void Unencrypted_FromAnUntrustedSender_ShowsTheWarning()
    {
        Assert.True(MailboxService.ShowsUnencryptedWarning(Row(false), new HashSet<string>()));
    }

    [Fact]
    public void TrustedSender_Encrypted_OrNotRecorded_ShowNoWarning()
    {
        var trusted = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "doctor@rediffmail.com" };
        Assert.False(MailboxService.ShowsUnencryptedWarning(Row(false), trusted));
        Assert.False(MailboxService.ShowsUnencryptedWarning(Row(true), new HashSet<string>()));
        Assert.False(MailboxService.ShowsUnencryptedWarning(Row(null), new HashSet<string>()));
    }
}
