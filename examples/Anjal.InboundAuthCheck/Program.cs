using System.Security.Cryptography;
using System.Text;
using Anjal.Auth;
using Anjal.Dkim;

namespace Anjal.Examples.InboundAuthCheck;

/// <summary>
/// Demonstrates inbound authentication end-to-end without requiring a live
/// DNS server. Three scenarios are run:
///   1. A message that's properly signed and verifiable.
///   2. A message with a tampered body (DKIM body-hash mismatch).
///   3. A message with no DKIM signature at all.
/// For each, the demo prints the verification verdicts and the formatted
/// Authentication-Results header that Anjal would prepend.
///
/// Note: SPF and DMARC verification require live DNS lookups, so this demo
/// shows only the DKIM path in isolation. To see all three working together,
/// configure Anjal as a real receiver and send messages to it from Gmail or
/// any major provider - the Authentication-Results header will appear in
/// the webhook payload.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        Console.WriteLine("=== Anjal inbound auth demo ===");
        Console.WriteLine();

        // 1. Generate a keypair (in production, this lives in the sender's infrastructure).
        using RSA rsa = RSA.Create(2048);
        string privatePem = rsa.ExportPkcs8PrivateKeyPem();
        byte[] publicDer = rsa.ExportSubjectPublicKeyInfo();

        // The signed message we'd send.
        const string originalBody = "Hello, this is a test.\r\n";
        const string baseHeaders =
            "From: noreply@example.test\r\n" +
            "To: recipient@anjal.test\r\n" +
            "Subject: Hello\r\n" +
            "Date: Mon, 19 May 2026 12:00:00 +0000\r\n" +
            "Message-ID: <demo@example.test>\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n";

        string originalMessage = baseHeaders + "\r\n" + originalBody;

        var signer = new DkimSigner(new DkimSigningOptions
        {
            HeaderCanon = HeaderCanonicalization.Relaxed,
            BodyCanon = BodyCanonicalization.Relaxed,
        });
        byte[] signedBytes = signer.Sign(Encoding.UTF8.GetBytes(originalMessage), new DkimKey
        {
            Domain = "example.test",
            Selector = "default",
            PrivateKeyPem = privatePem,
        });

        // 2. Verify each scenario using only the public key (the way a real receiver would).
        int failures = 0;

        Console.WriteLine("--- Scenario 1: Properly signed message ---");
        bool ok1 = VerifyManually(signedBytes, publicDer, expectPass: true);
        if (!ok1) failures++;
        Console.WriteLine();

        Console.WriteLine("--- Scenario 2: Body tampered after signing ---");
        // Take the signed bytes and modify the body. The bh= will no longer match.
        string tamperedText = Encoding.UTF8.GetString(signedBytes).Replace(
            "Hello, this is a test.", "Hello, this is TAMPERED.", StringComparison.Ordinal);
        byte[] tamperedBytes = Encoding.UTF8.GetBytes(tamperedText);
        bool ok2 = VerifyManually(tamperedBytes, publicDer, expectPass: false);
        if (!ok2) failures++;
        Console.WriteLine();

        Console.WriteLine("--- Scenario 3: Unsigned message ---");
        bool ok3 = VerifyManually(Encoding.UTF8.GetBytes(originalMessage), publicDer, expectPass: false, expectNoSignature: true);
        if (!ok3) failures++;
        Console.WriteLine();

        // 3. Build the Authentication-Results header for a sample passing message.
        Console.WriteLine("--- Sample Authentication-Results header ---");
        var spfDetail = new SpfDetail
        {
            Result = SpfResult.Pass,
            Domain = "example.test",
            PeerAddress = "192.0.2.5",
            MatchedMechanism = "ip4:192.0.2.0/24",
            Explanation = "192.0.2.5 authorized by example.test via ip4:192.0.2.0/24",
        };
        var dkimDetail = new DkimDetail
        {
            Result = DkimResult.Pass,
            Domain = "example.test",
            Selector = "default",
            Algorithm = "rsa-sha256",
            Explanation = "Signature verified against default._domainkey.example.test.",
        };
        var dmarcDetail = new DmarcDetail
        {
            Result = DmarcResult.Pass,
            FromDomain = "example.test",
            Policy = DmarcPolicy.Reject,
            SpfAligned = true,
            DkimAligned = true,
            AlignedDomain = "example.test",
            Explanation = "DMARC pass via DKIM alignment with example.test.",
        };

        string header = AuthenticationResultsBuilder.Build("mx.anjal.test", spfDetail, dkimDetail, dmarcDetail);
        Console.WriteLine("Authentication-Results: " + header);
        Console.WriteLine();

        // 4. Show what the JSON payload would look like.
        Console.WriteLine("--- Webhook payload authResults JSON ---");
        var results = new AuthenticationResults
        {
            ServingHost = "mx.anjal.test",
            Spf = spfDetail,
            Dkim = dkimDetail,
            Dmarc = dmarcDetail,
            HeaderValue = header,
        };
        Console.WriteLine(AuthResultsJson.Serialize(results));
        Console.WriteLine();

        if (failures > 0)
        {
            Console.WriteLine($"FAILED: {failures} scenarios did not match expectation.");
            return 1;
        }
        Console.WriteLine("SUCCESS: all three scenarios produced the expected verdict.");
        return 0;
    }

    /// <summary>
    /// Replicate what DkimVerifier does (minus the DNS lookup) so the demo
    /// can show the round-trip without needing a live DNS server.
    /// </summary>
    private static bool VerifyManually(byte[] messageBytes, byte[] publicDer, bool expectPass, bool expectNoSignature = false)
    {
        DkimMessage parsed;
        try { parsed = DkimMessage.Parse(messageBytes); }
        catch (FormatException ex)
        {
            Console.WriteLine($"  ERROR: parse failed: {ex.Message}");
            return false;
        }

        string? sigHeader = parsed.GetHeaderValue("DKIM-Signature");
        if (sigHeader is null)
        {
            Console.WriteLine("  Verdict: NONE (no DKIM-Signature header)");
            return expectNoSignature == true && expectPass == false;
        }

        // Parse tags.
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string p in sigHeader.Split(';'))
        {
            string t = p.Trim();
            if (t.Length == 0) continue;
            int eq = t.IndexOf('=');
            if (eq < 0) continue;
            tags[t.Substring(0, eq).Trim()] = t.Substring(eq + 1).Trim();
        }

        // bh= check (body hash).
        byte[] canonBody = DkimCanonicalizer.CanonBody(parsed.Body, BodyCanonicalization.Relaxed);
        byte[] computedBh = SHA256.HashData(canonBody);
        string computedBhBase64 = Convert.ToBase64String(computedBh);
        string declaredBh = tags["bh"];
        if (computedBhBase64 != declaredBh)
        {
            Console.WriteLine($"  Verdict: FAIL (body hash mismatch)");
            Console.WriteLine($"    expected bh= {declaredBh}");
            Console.WriteLine($"    computed bh= {computedBhBase64}");
            return expectPass == false;
        }

        // b= signature verification.
        int bIdx = sigHeader.LastIndexOf("b=", StringComparison.Ordinal);
        string bValue = sigHeader.Substring(bIdx + 2).Trim();
        byte[] signature = Convert.FromBase64String(bValue.Replace(" ", "").Replace("\r", "").Replace("\n", ""));

        string sigValueEmptyB = sigHeader.Substring(0, bIdx + 2);

        var signingInput = new StringBuilder();
        foreach (string h in tags["h"].Split(':'))
        {
            string headerName = h.Trim();
            string? value = parsed.GetHeaderValue(headerName);
            if (value is null) continue;
            signingInput.Append(DkimCanonicalizer.CanonHeader(headerName, value, HeaderCanonicalization.Relaxed));
        }
        string dkimCanon = DkimCanonicalizer.CanonHeader("DKIM-Signature", sigValueEmptyB, HeaderCanonicalization.Relaxed);
        if (dkimCanon.EndsWith("\r\n", StringComparison.Ordinal))
            dkimCanon = dkimCanon.Substring(0, dkimCanon.Length - 2);
        signingInput.Append(dkimCanon);

        using RSA verifier = RSA.Create();
        verifier.ImportSubjectPublicKeyInfo(publicDer, out _);
        bool valid = verifier.VerifyData(
            Encoding.UTF8.GetBytes(signingInput.ToString()),
            signature,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        Console.WriteLine($"  Verdict: {(valid ? "PASS" : "FAIL")} (RSA signature {(valid ? "valid" : "invalid")})");
        return valid == expectPass;
    }
}
