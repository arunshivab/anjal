using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Anjal.Auth.Tests;

public class Rfc8301Tests
{
    private const string Message =
        "From: a@ex.test\r\nTo: b@dest.test\r\nSubject: Signed\r\nDate: Mon, 21 Sep 2026 12:00:00 +0000\r\n" +
        "Message-ID: <s@ex.test>\r\n\r\nHello world.\r\n";

    private static (byte[] Signed, string PublicKeyBase64) Sign(int bits)
    {
        using RSA rsa = RSA.Create(bits);
        var signer = new Anjal.Dkim.DkimSigner();
        byte[] signed = signer.Sign(Encoding.UTF8.GetBytes(Message), new Anjal.Dkim.DkimKey
        {
            Domain = "ex.test",
            Selector = "default",
            PrivateKeyPem = rsa.ExportPkcs8PrivateKeyPem(),
        });
        return (signed, System.Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()));
    }

    private static byte[] Rewrite(byte[] signed, string from, string to) =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(signed).Replace(from, to, System.StringComparison.Ordinal));

    [Fact]
    public async System.Threading.Tasks.Task AFullSignature_Verifies_ThroughDns()
    {
        (byte[] signed, string key) = Sign(2048);
        using var dns = new FakeDnsServer(new() { ["default._domainkey.ex.test"] = "v=DKIM1; k=rsa; p=" + key });
        DkimDetail d = await new DkimVerifier(new Anjal.Dns.DnsResolver(dns.EndPoint)).VerifyAsync(signed);
        Assert.Equal(DkimResult.Pass, d.Result);
    }

    [Fact]
    public async System.Threading.Tasks.Task RsaSha1_IsNotAccepted()
    {
        (byte[] signed, string key) = Sign(2048);
        using var dns = new FakeDnsServer(new() { ["default._domainkey.ex.test"] = "v=DKIM1; k=rsa; p=" + key });
        DkimDetail d = await new DkimVerifier(new Anjal.Dns.DnsResolver(dns.EndPoint)).VerifyAsync(Rewrite(signed, "a=rsa-sha256", "a=rsa-sha1"));
        Assert.Equal(DkimResult.PermError, d.Result);
        Assert.Contains("8301", d.Explanation, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task AnExpiredSignature_IsNotAccepted()
    {
        (byte[] signed, string key) = Sign(2048);
        using var dns = new FakeDnsServer(new() { ["default._domainkey.ex.test"] = "v=DKIM1; k=rsa; p=" + key });
        byte[] expired = Rewrite(signed, "a=rsa-sha256", "x=1000000000; a=rsa-sha256");
        DkimDetail d = await new DkimVerifier(new Anjal.Dns.DnsResolver(dns.EndPoint)).VerifyAsync(expired);
        Assert.Equal(DkimResult.PermError, d.Result);
        Assert.Contains("expired", d.Explanation, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AKeyShorterThan1024Bits_IsNotAccepted()
    {
        using RSA rsa = RSA.Create(512);
        byte[] data = Encoding.ASCII.GetBytes("signed bytes");
        byte[] sig = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        DkimDetail d = DkimVerifier.CheckSignature(
            System.Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()), data, sig, HashAlgorithmName.SHA256, "ex.test", "default", "rsa-sha256");
        Assert.Equal(DkimResult.PermError, d.Result);
        Assert.Contains("512 bits", d.Explanation, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A2048BitKey_Verifies_ThroughTheSameCheck()
    {
        using RSA rsa = RSA.Create(2048);
        byte[] data = Encoding.ASCII.GetBytes("signed bytes");
        byte[] sig = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        DkimDetail d = DkimVerifier.CheckSignature(
            System.Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()), data, sig, HashAlgorithmName.SHA256, "ex.test", "default", "rsa-sha256");
        Assert.Equal(DkimResult.Pass, d.Result);
    }

    [Fact]
    public async System.Threading.Tasks.Task Spf_MoreThanTwoVoidLookups_IsPermError()
    {
        // .invalid never resolves (RFC 6761), so each exists: is a void lookup.
        using var dns = new FakeDnsServer(new()
        {
            ["voids.test"] = "v=spf1 exists:a.invalid exists:b.invalid exists:c.invalid -all",
        });
        SpfDetail d = await new SpfVerifier(new Anjal.Dns.DnsResolver(dns.EndPoint)).CheckAsync(IPAddress.Parse("203.0.113.9"), "voids.test");
        Assert.Equal(SpfResult.PermError, d.Result);
    }

    [Fact]
    public async System.Threading.Tasks.Task Spf_TwoVoidLookups_AreStillAllowed()
    {
        using var dns = new FakeDnsServer(new()
        {
            ["twovoids.test"] = "v=spf1 exists:a.invalid exists:b.invalid -all",
        });
        SpfDetail d = await new SpfVerifier(new Anjal.Dns.DnsResolver(dns.EndPoint)).CheckAsync(IPAddress.Parse("203.0.113.9"), "twovoids.test");
        Assert.Equal(SpfResult.Fail, d.Result);
    }
}

public class DnsReplySourceTests
{
    [Fact]
    public async System.Threading.Tasks.Task AReplyFromAnyOtherAddressOrPort_IsIgnored()
    {
        // The answer is correct in every respect except where it came from:
        // an off-path forger's reply looks exactly like this.
        using var dns = new FakeDnsServer(new() { ["x.test"] = "v=spf1 -all" }, answerFromAnotherPort: true);
        var resolver = new Anjal.Dns.DnsResolver(dns.EndPoint);
        await Assert.ThrowsAsync<Anjal.Dns.DnsException>(() => resolver.LookupTxtAsync("x.test"));
    }

    [Fact]
    public async System.Threading.Tasks.Task AReplyFromTheServerAsked_IsAccepted()
    {
        using var dns = new FakeDnsServer(new() { ["x.test"] = "v=spf1 -all" });
        var resolver = new Anjal.Dns.DnsResolver(dns.EndPoint);
        Assert.Contains("v=spf1 -all", await resolver.LookupTxtAsync("x.test"));
    }
}
