using System.Security.Cryptography;
using System.Text;

namespace Anjal.Auth.Tests;

/// <summary>
/// Cross-module integration: messages signed with <c>Anjal.Dkim.DkimSigner</c>
/// (PR 7) must verify with <c>Anjal.Auth.DkimVerifier</c> (PR 8). These tests
/// validate the canonicalization is interpreted the same way on both sides.
/// </summary>
public class DkimRoundTripTests
{
    [Fact]
    public void Signed_Body_Hash_Matches_Independent_Computation()
    {
        // Pure round-trip on bh= calculation: sign a message, then independently
        // compute SHA256 of the canonicalized body and verify the bh= tag matches.
        using RSA rsa = RSA.Create(2048);
        string pem = rsa.ExportPkcs8PrivateKeyPem();

        const string body = "Hello world.\r\n";
        const string message =
            "From: a@ex.test\r\n" +
            "To: b@dest.test\r\n" +
            "Subject: Round-trip\r\n" +
            "Date: Mon, 19 May 2026 12:00:00 +0000\r\n" +
            "Message-ID: <rt@ex.test>\r\n" +
            "\r\n" + body;

        var signer = new Anjal.Dkim.DkimSigner(
            new Anjal.Dkim.DkimSigningOptions
            {
                HeaderCanon = Anjal.Dkim.HeaderCanonicalization.Relaxed,
                BodyCanon = Anjal.Dkim.BodyCanonicalization.Relaxed,
            });

        byte[] signed = signer.Sign(Encoding.UTF8.GetBytes(message), new Anjal.Dkim.DkimKey
        {
            Domain = "ex.test",
            Selector = "default",
            PrivateKeyPem = pem,
        });

        // Extract bh=
        string signedText = Encoding.UTF8.GetString(signed);
        int bhIdx = signedText.IndexOf("bh=", System.StringComparison.Ordinal);
        int bhEnd = signedText.IndexOf(';', bhIdx);
        string bh = signedText.Substring(bhIdx + 3, bhEnd - bhIdx - 3).Trim();

        // Independent computation
        byte[] canonBody = Anjal.Dkim.DkimCanonicalizer.CanonBody(
            Encoding.UTF8.GetBytes(body), Anjal.Dkim.BodyCanonicalization.Relaxed);
        string expected = System.Convert.ToBase64String(SHA256.HashData(canonBody));

        Assert.Equal(expected, bh);
    }

    [Fact]
    public void Signed_Signature_Verifies_With_Public_Key()
    {
        // The full proof: produce a signed message and verify it the way a
        // receiving server would (with just the public key).
        using RSA rsa = RSA.Create(2048);
        string pem = rsa.ExportPkcs8PrivateKeyPem();
        byte[] publicDer = rsa.ExportSubjectPublicKeyInfo();

        const string message =
            "From: a@ex.test\r\n" +
            "To: b@dest.test\r\n" +
            "Subject: Test\r\n" +
            "Date: Mon, 19 May 2026 12:00:00 +0000\r\n" +
            "Message-ID: <rt@ex.test>\r\n" +
            "\r\n" +
            "Body content.\r\n";

        var signer = new Anjal.Dkim.DkimSigner(
            new Anjal.Dkim.DkimSigningOptions
            {
                HeaderCanon = Anjal.Dkim.HeaderCanonicalization.Relaxed,
                BodyCanon = Anjal.Dkim.BodyCanonicalization.Relaxed,
            });

        byte[] signed = signer.Sign(Encoding.UTF8.GetBytes(message), new Anjal.Dkim.DkimKey
        {
            Domain = "ex.test",
            Selector = "default",
            PrivateKeyPem = pem,
        });

        // Extract the DKIM-Signature header.
        string signedText = Encoding.UTF8.GetString(signed);
        int crlf = signedText.IndexOf("\r\n", System.StringComparison.Ordinal);
        string sigHeader = signedText.Substring(0, crlf);

        // Pull tags
        int firstColon = sigHeader.IndexOf(':', System.StringComparison.Ordinal);
        string sigValue = sigHeader.Substring(firstColon + 1).Trim();
        var tags = DkimVerifier.ParseTags(sigValue);

        // Get b= value
        int bIdx = sigValue.LastIndexOf("b=", System.StringComparison.Ordinal);
        string bValue = sigValue.Substring(bIdx + 2).Trim();
        byte[] signature = System.Convert.FromBase64String(bValue);

        // Build signing input the way DkimVerifier does
        string sigValueEmptyB = sigValue.Substring(0, bIdx + 2);

        var parsed = Anjal.Dkim.DkimMessage.Parse(signed);

        var signingInput = new StringBuilder();
        foreach (string h in tags["h"].Split(':', System.StringSplitOptions.RemoveEmptyEntries))
        {
            string headerName = h.Trim();
            string? value = parsed.GetHeaderValue(headerName);
            if (value is null) continue;
            signingInput.Append(Anjal.Dkim.DkimCanonicalizer.CanonHeader(
                headerName, value, Anjal.Dkim.HeaderCanonicalization.Relaxed));
        }
        string dkimCanon = Anjal.Dkim.DkimCanonicalizer.CanonHeader(
            "DKIM-Signature", sigValueEmptyB, Anjal.Dkim.HeaderCanonicalization.Relaxed);
        if (dkimCanon.EndsWith("\r\n", System.StringComparison.Ordinal))
        {
            dkimCanon = dkimCanon.Substring(0, dkimCanon.Length - 2);
        }
        signingInput.Append(dkimCanon);

        using RSA verifier = RSA.Create();
        verifier.ImportSubjectPublicKeyInfo(publicDer, out _);
        bool valid = verifier.VerifyData(
            Encoding.UTF8.GetBytes(signingInput.ToString()),
            signature,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        Assert.True(valid, "PR 7 signer's output must verify with the corresponding public key");
    }
}
