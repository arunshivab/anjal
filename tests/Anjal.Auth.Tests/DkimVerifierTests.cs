using System.Text;

namespace Anjal.Auth.Tests;

public class DkimVerifierTests
{
    [Fact]
    public void ParseTags_BasicSignature()
    {
        var tags = DkimVerifier.ParseTags("v=1; a=rsa-sha256; d=ex.test; s=default; h=From:To; bh=AAA; b=BBB");
        Assert.Equal("1", tags["v"]);
        Assert.Equal("rsa-sha256", tags["a"]);
        Assert.Equal("ex.test", tags["d"]);
        Assert.Equal("default", tags["s"]);
        Assert.Equal("From:To", tags["h"]);
        Assert.Equal("AAA", tags["bh"]);
        Assert.Equal("BBB", tags["b"]);
    }

    [Fact]
    public void ParseTags_ExtraWhitespace()
    {
        var tags = DkimVerifier.ParseTags(" v = 1 ;  a = rsa-sha256 ;");
        Assert.Equal("1", tags["v"]);
        Assert.Equal("rsa-sha256", tags["a"]);
    }

    [Fact]
    public void ParseTags_CaseInsensitiveKeys()
    {
        var tags = DkimVerifier.ParseTags("V=1; A=rsa-sha256; D=ex.test");
        Assert.Equal("1", tags["v"]);
        Assert.Equal("rsa-sha256", tags["a"]);
        Assert.Equal("ex.test", tags["d"]);
    }

    [Fact]
    public void ParseTags_TrailingSemicolon()
    {
        var tags = DkimVerifier.ParseTags("v=1; a=rsa-sha256;");
        Assert.Equal("1", tags["v"]);
        Assert.Equal("rsa-sha256", tags["a"]);
    }

    [Fact]
    public async System.Threading.Tasks.Task VerifyAsync_NoSignature_ReturnsNone()
    {
        // No DKIM-Signature header.
        const string message =
            "From: a@b.c\r\nTo: d@e.f\r\nSubject: Test\r\n\r\nBody\r\n";

        var dns = Anjal.Dns.DnsResolver.CreateFromSystem();
        var verifier = new DkimVerifier(dns);
        DkimDetail detail = await verifier.VerifyAsync(Encoding.UTF8.GetBytes(message));
        Assert.Equal(DkimResult.None, detail.Result);
    }

    [Fact]
    public async System.Threading.Tasks.Task VerifyAsync_MissingSignatureTag_ReturnsPermError()
    {
        // DKIM-Signature present but missing required tags.
        const string message =
            "DKIM-Signature: v=1; a=rsa-sha256\r\n" +
            "From: a@b.c\r\n\r\nBody\r\n";

        var dns = Anjal.Dns.DnsResolver.CreateFromSystem();
        var verifier = new DkimVerifier(dns);
        DkimDetail detail = await verifier.VerifyAsync(Encoding.UTF8.GetBytes(message));
        Assert.Equal(DkimResult.PermError, detail.Result);
    }

    [Fact]
    public async System.Threading.Tasks.Task VerifyAsync_UnsupportedAlgorithm_ReturnsPermError()
    {
        const string message =
            "DKIM-Signature: v=1; a=ed25519-sha256; d=ex.test; s=s; h=From; bh=AAA; b=BBB\r\n" +
            "From: a@ex.test\r\n\r\nBody\r\n";

        var dns = Anjal.Dns.DnsResolver.CreateFromSystem();
        var verifier = new DkimVerifier(dns);
        DkimDetail detail = await verifier.VerifyAsync(Encoding.UTF8.GetBytes(message));
        Assert.Equal(DkimResult.PermError, detail.Result);
        Assert.Equal("ed25519-sha256", detail.Algorithm);
    }
}
