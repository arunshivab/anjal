using System.Security.Cryptography;
using System.Text;

namespace Anjal.Dkim;

/// <summary>
/// Signs RFC 5322 messages with a DKIM-Signature header (RFC 6376).
/// Uses RSA-SHA256.
/// </summary>
public sealed class DkimSigner
{
    private readonly DkimSigningOptions options;
    private readonly System.Func<System.DateTimeOffset> clock;

    /// <summary>
    /// Construct a signer.
    /// </summary>
    /// <param name="options">Signing options. If null, defaults are used.</param>
    /// <param name="clock">Source of "now" for the <c>t=</c> tag. Test injection point.</param>
    public DkimSigner(DkimSigningOptions? options = null, System.Func<System.DateTimeOffset>? clock = null)
    {
        this.options = options ?? new DkimSigningOptions();
        this.clock = clock ?? (() => System.DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Sign a message and return the modified message with a
    /// <c>DKIM-Signature:</c> header prepended.
    /// </summary>
    /// <param name="rawBytes">The original RFC 5322 message bytes.</param>
    /// <param name="key">The DKIM key to sign with. The key's <see cref="DkimKey.Domain"/>
    /// becomes the <c>d=</c> tag; its <see cref="DkimKey.Selector"/> the <c>s=</c> tag.</param>
    /// <exception cref="System.ArgumentException">The key's PEM is malformed.</exception>
    /// <exception cref="System.FormatException">The message has no header/body separator.</exception>
    public byte[] Sign(byte[] rawBytes, DkimKey key)
    {
        System.ArgumentNullException.ThrowIfNull(rawBytes);
        System.ArgumentNullException.ThrowIfNull(key);
        if (string.IsNullOrEmpty(key.Domain)) throw new System.ArgumentException("Key has no domain.", nameof(key));
        if (string.IsNullOrEmpty(key.Selector)) throw new System.ArgumentException("Key has no selector.", nameof(key));
        if (string.IsNullOrEmpty(key.PrivateKeyPem)) throw new System.ArgumentException("Key has no private key PEM.", nameof(key));

        DkimMessage parsed = DkimMessage.Parse(rawBytes);

        // 1. Canonicalize body and hash it.
        byte[] canonBody = DkimCanonicalizer.CanonBody(parsed.Body, this.options.BodyCanon);
        byte[] bodyHash = SHA256.HashData(canonBody);
        string bh = System.Convert.ToBase64String(bodyHash);

        // 2. Resolve the header set to sign. Required: From. Order:
        //    deduplicate but preserve first-seen order from options.
        System.Collections.Generic.List<string> signedHeaders = BuildSignedHeaderList(parsed, this.options.SignedHeaders);

        // 3. Build the DKIM-Signature value with b= empty for signing.
        long t = this.clock().ToUnixTimeSeconds();
        string headerCanonTag = this.options.HeaderCanon == HeaderCanonicalization.Simple ? "simple" : "relaxed";
        string bodyCanonTag = this.options.BodyCanon == BodyCanonicalization.Simple ? "simple" : "relaxed";
        string canonTag = headerCanonTag + "/" + bodyCanonTag;
        string hTagValue = string.Join(":", signedHeaders);

        var sigBuilder = new StringBuilder();
        sigBuilder.Append("v=1; a=rsa-sha256; c=").Append(canonTag);
        sigBuilder.Append("; d=").Append(key.Domain);
        sigBuilder.Append("; s=").Append(key.Selector);
        sigBuilder.Append("; t=").Append(t.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (this.options.IncludeBodyLength)
        {
            sigBuilder.Append("; l=").Append(canonBody.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        sigBuilder.Append("; h=").Append(hTagValue);
        sigBuilder.Append("; bh=").Append(bh);
        sigBuilder.Append("; b=");
        string sigValueWithoutB = sigBuilder.ToString();

        // 4. Build the bytes to sign: the canonicalized signed headers
        //    plus the DKIM-Signature header (with b= empty) canonicalized,
        //    NO trailing CRLF after the DKIM-Signature line per RFC 6376
        //    section 3.7 step 4 ("without a trailing CRLF").
        var signingInput = new StringBuilder();
        foreach (string hdrName in signedHeaders)
        {
            string? value = parsed.GetHeaderValue(hdrName);
            if (value is null) continue; // Skip missing headers (signed-but-absent is allowed per RFC).
            signingInput.Append(DkimCanonicalizer.CanonHeader(hdrName, value, this.options.HeaderCanon));
        }
        // Append the DKIM-Signature line, canonicalized, without trailing CRLF.
        string dkimSigCanon = DkimCanonicalizer.CanonHeader("DKIM-Signature", sigValueWithoutB, this.options.HeaderCanon);
        if (dkimSigCanon.EndsWith("\r\n", System.StringComparison.Ordinal))
        {
            dkimSigCanon = dkimSigCanon.Substring(0, dkimSigCanon.Length - 2);
        }
        signingInput.Append(dkimSigCanon);

        byte[] dataToSign = Encoding.UTF8.GetBytes(signingInput.ToString());

        // 5. RSA-SHA256 sign.
        using RSA rsa = RSA.Create();
        rsa.ImportFromPem(key.PrivateKeyPem);
        byte[] signature = rsa.SignData(dataToSign, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        string b = System.Convert.ToBase64String(signature);

        // 6. Build the final DKIM-Signature header and prepend.
        string finalSigValue = sigValueWithoutB + b;
        string finalHeader = "DKIM-Signature: " + finalSigValue + "\r\n";

        return PrependHeader(rawBytes, finalHeader);
    }

    private static System.Collections.Generic.List<string> BuildSignedHeaderList(
        DkimMessage msg,
        System.Collections.Generic.IReadOnlyList<string> requested)
    {
        var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var result = new System.Collections.Generic.List<string>();

        // Always include From first if available.
        if (msg.GetHeaderValue("From") is not null)
        {
            result.Add("From");
            seen.Add("From");
        }

        foreach (string h in requested)
        {
            if (string.IsNullOrWhiteSpace(h)) continue;
            if (!seen.Add(h)) continue;
            // Include in the h= tag even if absent from message; verifiers
            // tolerate signed-but-missing headers (RFC 6376 section 5.4.2).
            result.Add(h);
        }

        return result;
    }

    private static byte[] PrependHeader(byte[] original, string header)
    {
        // Find the start of the message - just prepend the header bytes.
        byte[] headerBytes = Encoding.UTF8.GetBytes(header);
        byte[] result = new byte[headerBytes.Length + original.Length];
        System.Buffer.BlockCopy(headerBytes, 0, result, 0, headerBytes.Length);
        System.Buffer.BlockCopy(original, 0, result, headerBytes.Length, original.Length);
        return result;
    }
}
