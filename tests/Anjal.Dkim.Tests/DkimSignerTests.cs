using System.Security.Cryptography;
using System.Text;

namespace Anjal.Dkim.Tests;

public class DkimSignerTests
{
    private static readonly string[] StandardSignedHeaders = new[] { "From", "To", "Subject", "Date", "Message-ID" };
    private static readonly string[] SubjectDateOnly = new[] { "Subject", "Date" };

    private static (string privatePem, byte[] publicDer) GenerateKeypair()
    {
        using RSA rsa = RSA.Create(2048);
        return (rsa.ExportPkcs8PrivateKeyPem(), rsa.ExportSubjectPublicKeyInfo());
    }

    private const string SampleMessage =
        "From: noreply@example.test\r\n" +
        "To: dest@gmail.com\r\n" +
        "Subject: Hello\r\n" +
        "Date: Mon, 19 May 2026 12:00:00 +0000\r\n" +
        "Message-ID: <demo@example.test>\r\n" +
        "\r\n" +
        "Body content here.\r\n";

    private static System.Func<System.DateTimeOffset> FixedClock =>
        () => new System.DateTimeOffset(2026, 5, 19, 12, 0, 0, System.TimeSpan.Zero);

    [Fact]
    public void Sign_PrependsDkimSignatureHeader()
    {
        (string pem, _) = GenerateKeypair();
        var signer = new DkimSigner(new DkimSigningOptions { SignedHeaders = StandardSignedHeaders }, clock: FixedClock);

        byte[] signed = signer.Sign(Encoding.UTF8.GetBytes(SampleMessage), new DkimKey
        {
            Domain = "example.test",
            Selector = "default",
            PrivateKeyPem = pem,
        });

        string text = Encoding.UTF8.GetString(signed);
        Assert.StartsWith("DKIM-Signature: v=1; a=rsa-sha256;", text, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Sign_IncludesAllRequiredTags()
    {
        (string pem, _) = GenerateKeypair();
        var signer = new DkimSigner(new DkimSigningOptions { SignedHeaders = StandardSignedHeaders }, clock: FixedClock);

        byte[] signed = signer.Sign(Encoding.UTF8.GetBytes(SampleMessage), new DkimKey
        {
            Domain = "example.test",
            Selector = "s1",
            PrivateKeyPem = pem,
        });

        string text = Encoding.UTF8.GetString(signed);
        // RFC 6376 required tags: v= a= b= bh= d= h= s=
        Assert.Contains("v=1", text, System.StringComparison.Ordinal);
        Assert.Contains("a=rsa-sha256", text, System.StringComparison.Ordinal);
        Assert.Contains("c=relaxed/relaxed", text, System.StringComparison.Ordinal);
        Assert.Contains("d=example.test", text, System.StringComparison.Ordinal);
        Assert.Contains("s=s1", text, System.StringComparison.Ordinal);
        Assert.Contains("h=From:", text, System.StringComparison.Ordinal);
        Assert.Contains("bh=", text, System.StringComparison.Ordinal);
        Assert.Contains("b=", text, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Sign_BodyHashIsCorrect()
    {
        (string pem, _) = GenerateKeypair();
        var signer = new DkimSigner(new DkimSigningOptions { SignedHeaders = StandardSignedHeaders }, clock: FixedClock);

        byte[] signed = signer.Sign(Encoding.UTF8.GetBytes(SampleMessage), new DkimKey
        {
            Domain = "example.test",
            Selector = "default",
            PrivateKeyPem = pem,
        });

        string text = Encoding.UTF8.GetString(signed);

        // Extract bh=
        int bhStart = text.IndexOf("bh=", System.StringComparison.Ordinal);
        int bhEnd = text.IndexOf(';', bhStart);
        string bh = text.Substring(bhStart + 3, bhEnd - bhStart - 3);

        // Compute independently using the canonicalizer.
        byte[] canonBody = DkimCanonicalizer.CanonBody(
            Encoding.UTF8.GetBytes("Body content here.\r\n"),
            BodyCanonicalization.Relaxed);
        string expected = System.Convert.ToBase64String(SHA256.HashData(canonBody));

        Assert.Equal(expected, bh);
    }

    [Fact]
    public void Sign_SignatureVerifiesWithPublicKey()
    {
        // The crucial test: a third party with only the public key can verify
        // the signature, as a receiving mail server would.
        (string pem, byte[] pubDer) = GenerateKeypair();
        var signer = new DkimSigner(new DkimSigningOptions { SignedHeaders = StandardSignedHeaders }, clock: FixedClock);

        byte[] signed = signer.Sign(Encoding.UTF8.GetBytes(SampleMessage), new DkimKey
        {
            Domain = "example.test",
            Selector = "default",
            PrivateKeyPem = pem,
        });

        string text = Encoding.UTF8.GetString(signed);

        // Extract the DKIM-Signature header.
        int crlf = text.IndexOf("\r\n", System.StringComparison.Ordinal);
        string sigHeader = text.Substring(0, crlf);

        // Extract b= value.
        int bIdx = sigHeader.LastIndexOf("b=", System.StringComparison.Ordinal);
        string bValue = sigHeader.Substring(bIdx + 2);
        byte[] signature = System.Convert.FromBase64String(bValue);

        // Rebuild the signing input.
        string sigValueEmptyB = sigHeader.Substring("DKIM-Signature: ".Length, bIdx - "DKIM-Signature: ".Length + 2);

        var rebuild = new StringBuilder();
        foreach (string h in StandardSignedHeaders)
        {
            int hStart = SampleMessage.IndexOf(h + ":", System.StringComparison.OrdinalIgnoreCase);
            if (hStart < 0) continue;
            int hEnd = SampleMessage.IndexOf("\r\n", hStart, System.StringComparison.Ordinal);
            string val = SampleMessage.Substring(hStart + h.Length + 1, hEnd - hStart - h.Length - 1);
            rebuild.Append(DkimCanonicalizer.CanonHeader(h, val, HeaderCanonicalization.Relaxed));
        }
        string dkimCanon = DkimCanonicalizer.CanonHeader("DKIM-Signature", sigValueEmptyB, HeaderCanonicalization.Relaxed);
        if (dkimCanon.EndsWith("\r\n", System.StringComparison.Ordinal))
        {
            dkimCanon = dkimCanon.Substring(0, dkimCanon.Length - 2);
        }
        rebuild.Append(dkimCanon);

        using RSA verifier = RSA.Create();
        verifier.ImportSubjectPublicKeyInfo(pubDer, out _);
        bool valid = verifier.VerifyData(
            Encoding.UTF8.GetBytes(rebuild.ToString()),
            signature,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        Assert.True(valid, "Signature should verify with the corresponding public key");
    }

    [Fact]
    public void Sign_AlwaysIncludesFromHeader()
    {
        // From is required by RFC 6376 even if not in SignedHeaders list.
        (string pem, _) = GenerateKeypair();
        var signer = new DkimSigner(
            new DkimSigningOptions { SignedHeaders = SubjectDateOnly },
            clock: FixedClock);

        byte[] signed = signer.Sign(Encoding.UTF8.GetBytes(SampleMessage), new DkimKey
        {
            Domain = "example.test",
            Selector = "s1",
            PrivateKeyPem = pem,
        });

        string text = Encoding.UTF8.GetString(signed);
        Assert.Contains("h=From:", text, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Sign_ThrowsOnInvalidKeyPem()
    {
        var signer = new DkimSigner(clock: FixedClock);

        // RSA.ImportFromPem throws ArgumentException for unparseable input.
        // Catch the base Exception and just assert that SOME exception fires -
        // the precise type ("ArgumentException" vs "CryptographicException")
        // is a BCL implementation detail that has shifted between .NET versions.
        System.Exception? caught = null;
        try
        {
            signer.Sign(Encoding.UTF8.GetBytes(SampleMessage), new DkimKey
            {
                Domain = "example.test",
                Selector = "default",
                PrivateKeyPem = "not-a-pem",
            });
        }
#pragma warning disable CA1031
        catch (System.Exception ex)
        {
            caught = ex;
        }
#pragma warning restore CA1031

        Assert.NotNull(caught);
        Assert.True(
            caught is System.ArgumentException || caught is System.Security.Cryptography.CryptographicException,
            $"Expected ArgumentException or CryptographicException, got {caught!.GetType().Name}");
    }

    [Fact]
    public void Sign_ThrowsOnEmptyDomain()
    {
        (string pem, _) = GenerateKeypair();
        var signer = new DkimSigner(clock: FixedClock);

        Assert.Throws<System.ArgumentException>(() =>
        {
            signer.Sign(Encoding.UTF8.GetBytes(SampleMessage), new DkimKey
            {
                Domain = string.Empty,
                Selector = "default",
                PrivateKeyPem = pem,
            });
        });
    }
}
