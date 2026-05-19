using System.Security.Cryptography;
using System.Text;
using Anjal.Dkim;

namespace Anjal.Examples.DkimSigning;

/// <summary>
/// Demonstrates DKIM signing end-to-end:
///   1. Generate an RSA-2048 keypair in memory.
///   2. Sign a sample message.
///   3. Print the resulting <c>DKIM-Signature</c> header.
///   4. Verify the signature using only the public key, the way a
///      receiving mail server would.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        Console.WriteLine("=== DKIM signing demo ===");
        Console.WriteLine();

        // 1. Keypair.
        using RSA rsa = RSA.Create(2048);
        string privatePem = rsa.ExportPkcs8PrivateKeyPem();
        byte[] publicDer = rsa.ExportSubjectPublicKeyInfo();
        Console.WriteLine("Generated RSA-2048 keypair.");
        Console.WriteLine($"  Public key (would be published as DNS TXT, base64-encoded {publicDer.Length} bytes).");
        Console.WriteLine();

        // 2. Sample message.
        const string message =
            "From: noreply@mail.lipihis.in\r\n" +
            "To: patient@gmail.com\r\n" +
            "Subject: Appointment confirmation\r\n" +
            "Date: Mon, 19 May 2026 12:00:00 +0000\r\n" +
            "Message-ID: <demo-dkim@mail.lipihis.in>\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "\r\n" +
            "Hello,\r\n" +
            "\r\n" +
            "Your appointment is confirmed for May 21 at 10:00 AM.\r\n" +
            "\r\n" +
            "- Lipi HIS\r\n";
        byte[] messageBytes = Encoding.UTF8.GetBytes(message);

        // 3. Sign.
        var signer = new DkimSigner(
            new DkimSigningOptions
            {
                HeaderCanon = HeaderCanonicalization.Relaxed,
                BodyCanon = BodyCanonicalization.Relaxed,
            },
            clock: () => new DateTimeOffset(2026, 5, 19, 12, 0, 0, TimeSpan.Zero));

        byte[] signedBytes = signer.Sign(messageBytes, new DkimKey
        {
            Domain = "mail.lipihis.in",
            Selector = "default",
            PrivateKeyPem = privatePem,
        });
        string signed = Encoding.UTF8.GetString(signedBytes);

        Console.WriteLine("=== Signed message ===");
        foreach (string line in signed.Split("\r\n"))
        {
            Console.WriteLine($"  {line}");
        }

        // 4. Extract the DKIM-Signature header and verify with only the public key.
        int sigEnd = signed.IndexOf("\r\n", StringComparison.Ordinal);
        string firstLine = signed.Substring(0, sigEnd);
        Console.WriteLine("=== Verification ===");
        Console.WriteLine($"Extracted: {firstLine}");
        Console.WriteLine();

        // The body hash check.
        int bhStart = firstLine.IndexOf("bh=", StringComparison.Ordinal);
        int bhEnd = firstLine.IndexOf(';', bhStart);
        string bh = firstLine.Substring(bhStart + 3, bhEnd - bhStart - 3);

        byte[] canonBody = DkimCanonicalizer.CanonBody(
            Encoding.UTF8.GetBytes("Hello,\r\n\r\nYour appointment is confirmed for May 21 at 10:00 AM.\r\n\r\n- Lipi HIS\r\n"),
            BodyCanonicalization.Relaxed);
        string expectedBh = Convert.ToBase64String(SHA256.HashData(canonBody));
        bool bhMatch = bh == expectedBh;
        Console.WriteLine($"Body hash (bh=) check: {(bhMatch ? "VALID" : "INVALID")}");
        Console.WriteLine($"  expected: {expectedBh}");
        Console.WriteLine($"  got:      {bh}");
        Console.WriteLine();

        // The signature check.
        int bStart = firstLine.IndexOf("b=", bhEnd, StringComparison.Ordinal);
        string b = firstLine.Substring(bStart + 2);
        byte[] signature = Convert.FromBase64String(b);

        // Rebuild the signing input.
        string sigValueEmptyB = firstLine.Substring("DKIM-Signature: ".Length);
        sigValueEmptyB = sigValueEmptyB.Substring(0, sigValueEmptyB.IndexOf("b=", StringComparison.Ordinal) + 2);

        var rebuild = new StringBuilder();
        foreach (string h in new[] { "From", "To", "Subject", "Date", "Message-ID", "MIME-Version", "Content-Type" })
        {
            int hStart = message.IndexOf(h + ":", StringComparison.OrdinalIgnoreCase);
            if (hStart < 0) continue;
            int hEnd = message.IndexOf("\r\n", hStart, StringComparison.Ordinal);
            string val = message.Substring(hStart + h.Length + 1, hEnd - hStart - h.Length - 1);
            rebuild.Append(DkimCanonicalizer.CanonHeader(h, val, HeaderCanonicalization.Relaxed));
        }
        string dkimCanon = DkimCanonicalizer.CanonHeader("DKIM-Signature", sigValueEmptyB, HeaderCanonicalization.Relaxed);
        if (dkimCanon.EndsWith("\r\n", StringComparison.Ordinal))
        {
            dkimCanon = dkimCanon.Substring(0, dkimCanon.Length - 2);
        }
        rebuild.Append(dkimCanon);
        byte[] signingInput = Encoding.UTF8.GetBytes(rebuild.ToString());

        using RSA verifier = RSA.Create();
        verifier.ImportSubjectPublicKeyInfo(publicDer, out _);
        bool sigValid = verifier.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Console.WriteLine($"RSA signature (b=) check: {(sigValid ? "VALID" : "INVALID")}");
        Console.WriteLine();

        bool ok = bhMatch && sigValid;
        Console.WriteLine(ok
            ? "DKIM signing demo: SUCCESS - signed message verifies against the public key."
            : "DKIM signing demo: FAILED");
        return ok ? 0 : 1;
    }
}
