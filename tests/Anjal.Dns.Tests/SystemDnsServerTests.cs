using System.Net;
using System.Net.NetworkInformation;

namespace Anjal.Dns.Tests;

/// <summary>
/// DEF-048: the mail server aborted at startup on the production VM. Finding
/// the system DNS server enumerated network interfaces, which on Linux needs a
/// netlink socket, and the systemd unit's RestrictAddressFamilies refused it:
/// NetworkInformationException (97) "Address family not supported by protocol".
/// The server list now comes from /etc/resolv.conf, and no source may throw.
/// </summary>
public class SystemDnsServerTests
{
    // /etc/resolv.conf exactly as measured on the E2E node on 26 Sep 2026:
    // written by OpenNebula one-context, no systemd-resolved stub, each
    // server listed twice.
    private const string E2EResolvConf =
        "nameserver 8.8.8.8\n" +
        "nameserver 8.8.4.4\n" +
        "nameserver 8.8.8.8\n" +
        "nameserver 8.8.4.4\n" +
        "domain ssdcloudindia.net\n";

    private static readonly IPAddress[] InterfaceServers = { IPAddress.Parse("10.0.0.53") };

    [Fact]
    public void ParseResolvConf_E2ENode_ReturnsFirstServer()
    {
        Assert.Equal(IPAddress.Parse("8.8.8.8"), SystemDns(E2EResolvConf));
    }

    [Fact]
    public void ParseResolvConf_SkipsCommentsAndIpv6_AndHandlesCrLf()
    {
        string text = "# generated\r\n; also a comment\r\nsearch example.com\r\n" +
                      "nameserver 2001:4860:4860::8888\r\n  nameserver   127.0.0.53  # stub\r\n";
        Assert.Equal(IPAddress.Parse("127.0.0.53"), SystemDns(text));
    }

    [Fact]
    public void ParseResolvConf_NoIpv4Server_ReturnsNull()
    {
        Assert.Null(SystemDns("search example.com\nnameserver ::1\n"));
        Assert.Null(SystemDns(string.Empty));
    }

    [Fact]
    public void ResolvConf_WinsOverInterfaceEnumeration_WhichIsNotEvenCalled()
    {
        bool enumerated = false;
        IPAddress? found = DnsResolver.FindSystemDnsServer(
            () => E2EResolvConf,
            () => { enumerated = true; return InterfaceServers; });
        Assert.Equal(IPAddress.Parse("8.8.8.8"), found);
        Assert.False(enumerated);
    }

    [Fact]
    public void Enumeration_RefusedAsUnderSystemd_DoesNotThrow()
    {
        // The production failure: no resolv.conf answer, and enumeration
        // refused by the kernel with EAFNOSUPPORT (97).
        IPAddress? found = DnsResolver.FindSystemDnsServer(
            () => null,
            () => throw new NetworkInformationException(97));
        Assert.Null(found);
    }

    [Fact]
    public void ResolvConfUnreadable_FallsBackToEnumeration()
    {
        IPAddress? found = DnsResolver.FindSystemDnsServer(
            () => throw new System.UnauthorizedAccessException(),
            () => InterfaceServers);
        Assert.Equal(IPAddress.Parse("10.0.0.53"), found);
    }

    [Fact]
    public void CreateFromSystem_OnThisMachine_DoesNotThrow()
    {
        DnsResolver resolver = DnsResolver.CreateFromSystem();
        Assert.NotNull(resolver);
    }

    private static IPAddress? SystemDns(string resolvConf) =>
        DnsResolver.FindSystemDnsServer(() => resolvConf, () => System.Array.Empty<IPAddress>());
}
