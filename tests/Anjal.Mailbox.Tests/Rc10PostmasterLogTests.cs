using Anjal.Store;

namespace Anjal.Mailbox.Tests;

/// <summary>
/// v1.0.0-rc.10 (DEF-082): the start-up line saying where postmaster@ and
/// abuse@ at the server's own name go, built from the delivery's own rule.
/// </summary>
public sealed class Rc10PostmasterLogTests
{
    private static MailboxSink Sink(string? host, string? op) =>
        new(new InMemoryMessageStore(), new MaildirStore(System.IO.Path.GetTempPath(), "test")) { ServerHostName = host, OperatorPostmaster = op };

    [Fact]
    public void WithTheSetting_NamesTheOperatorAddress()
    {
        Assert.Equal("Postmaster: postmaster@ and abuse@ at mail.anjal.co.in go to ops@anjal.co.in (ANJAL_POSTMASTER).",
            Sink("mail.anjal.co.in", "ops@anjal.co.in").DescribeServerRoleAddresses());
    }

    [Fact]
    public void WithoutTheSetting_NamesTheParentDomain()
    {
        Assert.Equal("Postmaster: postmaster@ and abuse@ at mail.anjal.co.in go to postmaster@anjal.co.in (the parent domain; ANJAL_POSTMASTER not set), then to that domain's designated mailbox.",
            Sink("mail.anjal.co.in", null).DescribeServerRoleAddresses());
    }

    [Fact]
    public void WithoutAServerName_SaysSo()
    {
        Assert.StartsWith("Postmaster: no server name set", Sink(null, null).DescribeServerRoleAddresses(), System.StringComparison.Ordinal);
    }
}
