using System.Net;
using System.Net.Sockets;

namespace Anjal.Auth.Tests;

public class SpfVerifierTests
{
    [Fact]
    public async System.Threading.Tasks.Task CheckAsync_EmptyDomain_ReturnsNone()
    {
        // Empty MAIL FROM domain (legitimate for bounce messages per RFC
        // 5321 section 4.5.5) must short-circuit to None without hitting
        // DNS or crashing.
        var dns = Anjal.Dns.DnsResolver.CreateFromSystem();
        var verifier = new SpfVerifier(dns);

        SpfDetail detail = await verifier.CheckAsync(IPAddress.Parse("192.0.2.5"), string.Empty);
        Assert.Equal(SpfResult.None, detail.Result);
    }

    [Fact]
    public void MatchesCidr_Ipv4_24Bit_Match()
    {
        // Use reflection to test internal CIDR matching - it's a pure
        // function that doesn't touch DNS so it's safe to unit-test directly.
        var method = typeof(SpfVerifier).GetMethod("MatchesCidr",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        bool result = (bool)method!.Invoke(null, new object[]
        {
            IPAddress.Parse("192.0.2.5"),
            "192.0.2.0/24",
            AddressFamily.InterNetwork,
        })!;
        Assert.True(result);
    }

    [Fact]
    public void MatchesCidr_Ipv4_24Bit_NoMatch()
    {
        var method = typeof(SpfVerifier).GetMethod("MatchesCidr",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        bool result = (bool)method!.Invoke(null, new object[]
        {
            IPAddress.Parse("192.0.3.5"),
            "192.0.2.0/24",
            AddressFamily.InterNetwork,
        })!;
        Assert.False(result);
    }

    [Fact]
    public void MatchesCidr_Ipv4_ExactMatch_NoMask()
    {
        var method = typeof(SpfVerifier).GetMethod("MatchesCidr",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        bool result = (bool)method!.Invoke(null, new object[]
        {
            IPAddress.Parse("192.0.2.5"),
            "192.0.2.5",  // No CIDR mask = exact match
            AddressFamily.InterNetwork,
        })!;
        Assert.True(result);
    }

    [Fact]
    public void MatchesCidr_Ipv4_32BitMask()
    {
        var method = typeof(SpfVerifier).GetMethod("MatchesCidr",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        bool result = (bool)method!.Invoke(null, new object[]
        {
            IPAddress.Parse("192.0.2.5"),
            "192.0.2.5/32",
            AddressFamily.InterNetwork,
        })!;
        Assert.True(result);
    }

    [Fact]
    public void MatchesCidr_Ipv4_0BitMask_AlwaysMatches()
    {
        var method = typeof(SpfVerifier).GetMethod("MatchesCidr",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        bool result = (bool)method!.Invoke(null, new object[]
        {
            IPAddress.Parse("203.0.113.1"),
            "0.0.0.0/0",
            AddressFamily.InterNetwork,
        })!;
        Assert.True(result);
    }

    [Fact]
    public void MatchesCidr_Ipv4VsIpv6_NoMatch()
    {
        var method = typeof(SpfVerifier).GetMethod("MatchesCidr",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        bool result = (bool)method!.Invoke(null, new object[]
        {
            IPAddress.Parse("2001:db8::1"),
            "192.0.2.0/24",
            AddressFamily.InterNetwork,
        })!;
        Assert.False(result);
    }
}
