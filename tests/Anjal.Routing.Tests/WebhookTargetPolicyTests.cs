using System.Net;

namespace Anjal.Routing.Tests;

public class WebhookTargetPolicyTests
{
    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.1.1")]
    [InlineData("192.168.1.10")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:10.0.0.1")]
    public void PrivateAndSpecialAddresses_AreNotPublic(string address)
    {
        Assert.False(WebhookTargetPolicy.IsPublic(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void PublicAddresses_AreAllowed(string address)
    {
        Assert.True(WebhookTargetPolicy.IsPublic(IPAddress.Parse(address)));
    }

    [Fact]
    public void Default_RequiresHttpsAndRefusesPrivateLiterals()
    {
        var policy = new WebhookTargetPolicy();
        Assert.Null(policy.Validate("https://hooks.example.com/anjal"));
        Assert.NotNull(policy.Validate("http://hooks.example.com/anjal"));
        Assert.NotNull(policy.Validate("https://10.0.0.5/hook"));
        Assert.NotNull(policy.Validate("https://[::1]/hook"));
        Assert.NotNull(policy.Validate("https://user:pw@hooks.example.com/"));
        Assert.NotNull(policy.Validate("ftp://hooks.example.com/"));
        Assert.NotNull(policy.Validate("not a url"));
        Assert.NotNull(policy.Validate("https://example.com/" + new string('a', WebhookTargetPolicy.MaxUrlLength)));
    }

    [Fact]
    public void ExplicitOptIns_WidenThePolicy()
    {
        var lan = new WebhookTargetPolicy { AllowHttp = true, AllowPrivateAddresses = true };
        Assert.Null(lan.Validate("http://10.0.0.5:8080/hook"));
        Assert.True(lan.IsAllowedAddress(IPAddress.Loopback));
    }

    [Fact]
    public void Client_DoesNotFollowRedirects()
    {
        using HttpClient client = new WebhookTargetPolicy().CreateClient(System.TimeSpan.FromSeconds(5));
        Assert.Equal(System.TimeSpan.FromSeconds(5), client.Timeout);
    }
}
