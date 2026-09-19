using Anjal.Smtp;

namespace Anjal.Spam.Tests;

public class SmtpPolicyTests
{
    private sealed class Clock
    {
        public System.DateTimeOffset Now { get; set; } = new(2026, 9, 19, 10, 0, 0, System.TimeSpan.Zero);

        public void Advance(System.TimeSpan by) => this.Now += by;
    }

    [Fact]
    public void RateLimiter_Connections_SlidingWindow()
    {
        var clock = new Clock();
        var limiter = new RateLimiter(new RateLimitOptions { ConnectionsPerMinute = 3 }, () => clock.Now);

        Assert.True(limiter.OnConnect("1.2.3.4").Allowed);
        Assert.True(limiter.OnConnect("1.2.3.4").Allowed);
        Assert.True(limiter.OnConnect("1.2.3.4").Allowed);
        PolicyDecision fourth = limiter.OnConnect("1.2.3.4");
        Assert.False(fourth.Allowed);
        Assert.Equal(421, fourth.ReplyCode);
        Assert.True(limiter.OnConnect("5.6.7.8").Allowed, "other IPs are independent");

        clock.Advance(System.TimeSpan.FromSeconds(61));
        Assert.True(limiter.OnConnect("1.2.3.4").Allowed);
    }

    [Fact]
    public void RateLimiter_Messages_PerIpAndPerUser()
    {
        var clock = new Clock();
        var limiter = new RateLimiter(new RateLimitOptions { MessagesPerHourPerIp = 2, MessagesPerHourPerUser = 1 }, () => clock.Now);

        Assert.True(limiter.OnMailFrom("1.2.3.4", null, "a@b").Allowed);
        Assert.True(limiter.OnMailFrom("1.2.3.4", null, "a@b").Allowed);
        Assert.False(limiter.OnMailFrom("1.2.3.4", null, "a@b").Allowed);

        Assert.True(limiter.OnMailFrom("1.2.3.4", "arun@anjal.co.in", "arun@anjal.co.in").Allowed);
        PolicyDecision d = limiter.OnMailFrom("9.9.9.9", "ARUN@anjal.co.in", "arun@anjal.co.in");
        Assert.False(d.Allowed);
        Assert.Equal(451, d.ReplyCode);
        Assert.True(limiter.OnRcptTo("1.2.3.4", null, "a@b", "c@d").Allowed);

        clock.Advance(System.TimeSpan.FromHours(1) + System.TimeSpan.FromSeconds(1));
        Assert.True(limiter.OnMailFrom("1.2.3.4", "arun@anjal.co.in", "arun@anjal.co.in").Allowed);
    }

    [Fact]
    public void RateLimiter_ZeroDisables()
    {
        var limiter = new RateLimiter(new RateLimitOptions { ConnectionsPerMinute = 0, MessagesPerHourPerIp = 0, MessagesPerHourPerUser = 0 });
        for (int i = 0; i < 500; i++)
        {
            Assert.True(limiter.OnConnect("1.2.3.4").Allowed);
            Assert.True(limiter.OnMailFrom("1.2.3.4", null, "a@b").Allowed);
        }
    }

    [Fact]
    public void Greylist_DefersFirstContact_PassesAfterDelay_RemembersTriplet()
    {
        var clock = new Clock();
        var grey = new Greylist(new GreylistOptions { Delay = System.TimeSpan.FromMinutes(5) }, () => clock.Now);

        PolicyDecision first = grey.OnRcptTo("203.0.113.10", null, "a@example.com", "arun@anjal.co.in");
        Assert.False(first.Allowed);
        Assert.Equal(451, first.ReplyCode);
        Assert.Contains("Greylisted", first.ReplyText, System.StringComparison.Ordinal);

        clock.Advance(System.TimeSpan.FromMinutes(2));
        Assert.False(grey.OnRcptTo("203.0.113.10", null, "a@example.com", "arun@anjal.co.in").Allowed);

        clock.Advance(System.TimeSpan.FromMinutes(4));
        Assert.True(grey.OnRcptTo("203.0.113.10", null, "a@example.com", "arun@anjal.co.in").Allowed);
        Assert.True(grey.OnRcptTo("203.0.113.77", null, "A@Example.com", "ARUN@anjal.co.in").Allowed, "same /24 and case-insensitive triplet passes");

        Assert.False(grey.OnRcptTo("203.0.113.10", null, "other@example.com", "arun@anjal.co.in").Allowed, "new sender is a new triplet");
        Assert.Equal(2, grey.Count);
    }

    [Fact]
    public void Greylist_ExemptsAuthenticatedAndPrivateClients()
    {
        var grey = new Greylist();
        Assert.True(grey.OnRcptTo("203.0.113.10", "arun@anjal.co.in", "a@b", "c@d").Allowed);
        Assert.True(grey.OnRcptTo("127.0.0.1", null, "a@b", "c@d").Allowed);
        Assert.True(grey.OnRcptTo("10.1.2.3", null, "a@b", "c@d").Allowed);
        Assert.True(grey.OnRcptTo("192.168.1.9", null, "a@b", "c@d").Allowed);
        Assert.Equal(0, grey.Count);
    }

    [Fact]
    public void Greylist_NetworkKey_V4AndV6()
    {
        Assert.Equal("203.0.113.0/24", Greylist.NetworkKey("203.0.113.77"));
        Assert.Equal("2001:db8:1:2::/64", Greylist.NetworkKey("2001:db8:1:2:aaaa:bbbb:cccc:dddd"));
        Assert.Equal("not-an-ip", Greylist.NetworkKey("not-an-ip"));
    }

    [Fact]
    public void Composite_FirstRefusalWins()
    {
        var clock = new Clock();
        var limiter = new RateLimiter(new RateLimitOptions { ConnectionsPerMinute = 1 }, () => clock.Now);
        var grey = new Greylist(null, () => clock.Now);
        var composite = new CompositeSmtpPolicy(limiter, grey);

        Assert.True(composite.OnConnect("203.0.113.10").Allowed);
        Assert.False(composite.OnConnect("203.0.113.10").Allowed);
        Assert.True(composite.OnMailFrom("203.0.113.10", null, "a@b").Allowed);
        Assert.False(composite.OnRcptTo("203.0.113.10", null, "a@b", "c@d").Allowed);
    }

    [Fact]
    public void ModuleInfo_IsSet()
    {
        Assert.Equal("Anjal.Spam", ModuleInfo.Name);
        Assert.False(string.IsNullOrWhiteSpace(ModuleInfo.Version));
    }
}
