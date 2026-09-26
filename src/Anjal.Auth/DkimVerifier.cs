using System.Security.Cryptography;
using System.Text;

namespace Anjal.Auth;

/// <summary>
/// Verifies DKIM signatures per RFC 6376. Reuses the canonicalizer from
/// <c>Anjal.Dkim</c>. Supports the algorithms <c>rsa-sha256</c> and the
/// historically-required-but-deprecated <c>rsa-sha1</c> (verify-only;
/// signing only uses rsa-sha256).
/// </summary>
public sealed class DkimVerifier
{
    /// <summary>The shortest RSA key accepted (RFC 8301 section 3.2).</summary>
    public const int MinimumKeyBits = 1024;

    private readonly Anjal.Dns.DnsResolver dns;

    /// <summary>Construct.</summary>
    /// <param name="dns">DNS resolver for fetching the <c>_domainkey</c> TXT record.</param>
    public DkimVerifier(Anjal.Dns.DnsResolver dns)
    {
        System.ArgumentNullException.ThrowIfNull(dns);
        this.dns = dns;
    }

    /// <summary>The most signatures checked on one message. RFC 6376 section
    /// 6.1 lets a verifier limit this so a message cannot make it do
    /// unbounded work.</summary>
    public const int MaxSignaturesChecked = 5;

    /// <summary>
    /// Verify a message's DKIM-Signature headers, top first, up to
    /// <see cref="MaxSignaturesChecked"/>. The result reported is a passing
    /// signature whose domain matches the From domain (the one DMARC can use);
    /// failing that, any passing signature; failing that, the first signature's
    /// result. DEF-057: only the first signature used to be checked, so a
    /// broken or unaligned first signature hid a valid aligned second one.
    /// </summary>
    /// <param name="messageBytes">Full RFC 5322 message bytes.</param>
    /// <param name="ct">Cancellation.</param>
    public async System.Threading.Tasks.Task<DkimDetail> VerifyAsync(
        byte[] messageBytes,
        System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(messageBytes);

        // Parse the message into headers and body.
        Anjal.Dkim.DkimMessage parsed;
        try
        {
            parsed = Anjal.Dkim.DkimMessage.Parse(messageBytes);
        }
        catch (System.FormatException ex)
        {
            return new DkimDetail
            {
                Result = DkimResult.PermError,
                Explanation = $"Message parse failed: {ex.Message}",
            };
        }

        var signatures = new System.Collections.Generic.List<Anjal.Dkim.RawHeader>();
        foreach (Anjal.Dkim.RawHeader header in parsed.Headers)
        {
            if (string.Equals(header.Name, "DKIM-Signature", System.StringComparison.OrdinalIgnoreCase))
            {
                signatures.Add(header);
                if (signatures.Count == MaxSignaturesChecked)
                {
                    break;
                }
            }
        }
        if (signatures.Count == 0)
        {
            return new DkimDetail
            {
                Result = DkimResult.None,
                Explanation = "No DKIM-Signature header found.",
            };
        }

        string fromDomain = FromDomainOf(parsed);
        DkimDetail? first = null;
        DkimDetail? firstPass = null;
        foreach (Anjal.Dkim.RawHeader signature in signatures)
        {
            DkimDetail detail = await this.VerifyOneAsync(parsed, signature, ct).ConfigureAwait(false);
            first ??= detail;
            if (detail.Result == DkimResult.Pass)
            {
                if (IsAligned(detail.Domain, fromDomain))
                {
                    return detail;
                }
                firstPass ??= detail;
            }
        }
        return firstPass ?? first!;
    }

    /// <summary>True when the signing domain and the From domain are the same
    /// or one is a subdomain of the other - the relaxed alignment DMARC checks.</summary>
    internal static bool IsAligned(string? signingDomain, string fromDomain)
    {
        if (string.IsNullOrEmpty(signingDomain) || string.IsNullOrEmpty(fromDomain))
        {
            return false;
        }
        string d = signingDomain.TrimEnd('.').ToLowerInvariant();
        string f = fromDomain.TrimEnd('.').ToLowerInvariant();
        return d == f
            || f.EndsWith("." + d, System.StringComparison.Ordinal)
            || d.EndsWith("." + f, System.StringComparison.Ordinal);
    }

    private static string FromDomainOf(Anjal.Dkim.DkimMessage parsed)
    {
        string? from = parsed.GetHeaderValue("From");
        if (from is null)
        {
            return string.Empty;
        }
        int lt = from.IndexOf('<', System.StringComparison.Ordinal);
        int gt = from.IndexOf('>', System.StringComparison.Ordinal);
        string address = (lt >= 0 && gt > lt) ? from.Substring(lt + 1, gt - lt - 1) : from.Trim();
        int at = address.LastIndexOf('@');
        return at < 0 ? string.Empty : address.Substring(at + 1).Trim().ToLowerInvariant();
    }

    private async System.Threading.Tasks.Task<DkimDetail> VerifyOneAsync(
        Anjal.Dkim.DkimMessage parsed,
        Anjal.Dkim.RawHeader signatureHeader,
        System.Threading.CancellationToken ct)
    {
        string rawSig = signatureHeader.Value;

        // Parse the tag-list.
        System.Collections.Generic.Dictionary<string, string> tags = ParseTags(rawSig);
        if (!tags.TryGetValue("v", out string? v) || v != "1")
        {
            return new DkimDetail
            {
                Result = DkimResult.PermError,
                Explanation = "DKIM-Signature has missing or unsupported version.",
            };
        }
        if (!tags.TryGetValue("a", out string? algorithm))
        {
            return new DkimDetail { Result = DkimResult.PermError, Explanation = "Missing a= (algorithm) tag." };
        }
        if (!tags.TryGetValue("d", out string? domain))
        {
            return new DkimDetail { Result = DkimResult.PermError, Explanation = "Missing d= (domain) tag." };
        }
        if (!tags.TryGetValue("s", out string? selector))
        {
            return new DkimDetail { Result = DkimResult.PermError, Explanation = "Missing s= (selector) tag." };
        }
        if (!tags.TryGetValue("h", out string? signedHeaderList))
        {
            return new DkimDetail { Result = DkimResult.PermError, Explanation = "Missing h= (signed headers) tag." };
        }
        if (!tags.TryGetValue("bh", out string? bodyHashBase64))
        {
            return new DkimDetail { Result = DkimResult.PermError, Explanation = "Missing bh= (body hash) tag." };
        }
        if (!tags.TryGetValue("b", out string? signatureBase64))
        {
            return new DkimDetail { Result = DkimResult.PermError, Explanation = "Missing b= (signature) tag." };
        }

        // x= (RFC 6376 section 3.5): a signature past its expiry is not
        // valid, however well it verifies.
        if (tags.TryGetValue("x", out string? expiryText) &&
            long.TryParse(expiryText.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long expiry) &&
            expiry < System.DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            return new DkimDetail
            {
                Result = DkimResult.PermError,
                Domain = domain,
                Selector = selector,
                Algorithm = algorithm,
                Explanation = "Signature has expired (x= is in the past).",
            };
        }

        HashAlgorithmName hashAlg;
        if (string.Equals(algorithm, "rsa-sha256", System.StringComparison.OrdinalIgnoreCase))
        {
            hashAlg = HashAlgorithmName.SHA256;
        }
        else if (string.Equals(algorithm, "rsa-sha1", System.StringComparison.OrdinalIgnoreCase))
        {
            // RFC 8301 section 3.1: verifiers MUST NOT consider rsa-sha1
            // signatures valid. SHA-1 collisions are practical, and a forged
            // DKIM pass would carry DMARC with it.
            return new DkimDetail
            {
                Result = DkimResult.PermError,
                Domain = domain,
                Selector = selector,
                Algorithm = algorithm,
                Explanation = "rsa-sha1 signatures are not accepted (RFC 8301).",
            };
        }
        else
        {
            return new DkimDetail
            {
                Result = DkimResult.PermError,
                Domain = domain,
                Selector = selector,
                Algorithm = algorithm,
                Explanation = $"Unsupported algorithm: {algorithm}.",
            };
        }

        // Canonicalization tag is "c=header/body" or just "c=header" (body defaults
        // to simple). Default is simple/simple if c= is absent.
        Anjal.Dkim.HeaderCanonicalization headerCanon = Anjal.Dkim.HeaderCanonicalization.Simple;
        Anjal.Dkim.BodyCanonicalization bodyCanon = Anjal.Dkim.BodyCanonicalization.Simple;
        if (tags.TryGetValue("c", out string? canon))
        {
            string[] parts = canon.Split('/');
            if (parts.Length >= 1 && string.Equals(parts[0], "relaxed", System.StringComparison.OrdinalIgnoreCase))
            {
                headerCanon = Anjal.Dkim.HeaderCanonicalization.Relaxed;
            }
            if (parts.Length >= 2 && string.Equals(parts[1], "relaxed", System.StringComparison.OrdinalIgnoreCase))
            {
                bodyCanon = Anjal.Dkim.BodyCanonicalization.Relaxed;
            }
        }

        // Body hash check first - if this fails, body was modified in transit.
        byte[] canonBody = Anjal.Dkim.DkimCanonicalizer.CanonBody(parsed.Body, bodyCanon);
#pragma warning disable CA5350 // SHA1 is verify-only here for DKIM rsa-sha1 legacy support.
        byte[] computedBodyHash = hashAlg == HashAlgorithmName.SHA256
            ? SHA256.HashData(canonBody)
            : SHA1.HashData(canonBody);  // rsa-sha1 - verify-only support
#pragma warning restore CA5350
        string computedBhBase64 = System.Convert.ToBase64String(computedBodyHash);
        if (computedBhBase64 != bodyHashBase64)
        {
            return new DkimDetail
            {
                Result = DkimResult.Fail,
                Domain = domain,
                Selector = selector,
                Algorithm = algorithm,
                Explanation = "Body hash (bh=) mismatch - body modified in transit.",
            };
        }

        // Fetch the public key.
        string publicKeyBase64;
        try
        {
            publicKeyBase64 = await this.FetchPublicKeyAsync(domain, selector, ct).ConfigureAwait(false);
        }
        catch (DkimKeyFetchError ex)
        {
            return new DkimDetail
            {
                Result = ex.Transient ? DkimResult.TempError : DkimResult.PermError,
                Domain = domain,
                Selector = selector,
                Algorithm = algorithm,
                Explanation = ex.Message,
            };
        }

        // Build the signing input: canonicalized signed headers + canonicalized
        // DKIM-Signature header with b= empty, NO trailing CRLF.
        // DEF-056, RFC 6376 section 5.4.2: each listing of a name takes the
        // next unused instance of that header from the bottom of the message;
        // a name listed more times than it occurs contributes nothing after its
        // last instance (Gmail lists from, to, subject and more twice, and names
        // absent headers such as cc, to stop them being added later). The
        // header is hashed as written in the message - its own name, not the
        // spelling in h= - which simple canonicalization depends on.
        string[] signedHeaders = signedHeaderList.Split(':', System.StringSplitOptions.RemoveEmptyEntries);
        var signingInput = new StringBuilder();
        var instancesUsed = new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        foreach (string h in signedHeaders)
        {
            string headerName = h.Trim();
            instancesUsed.TryGetValue(headerName, out int alreadyUsed);
            instancesUsed[headerName] = alreadyUsed + 1;
            Anjal.Dkim.RawHeader? header = NthFromBottom(parsed, headerName, alreadyUsed);
            if (header is null)
            {
                continue;
            }
            signingInput.Append(Anjal.Dkim.DkimCanonicalizer.CanonHeader(header.Name, header.Value, headerCanon));
        }
        // The DKIM-Signature header itself with b= replaced by empty string.
        string sigValueEmptyB = RemoveBTagValue(rawSig);
        string dkimCanon = Anjal.Dkim.DkimCanonicalizer.CanonHeader(signatureHeader.Name, sigValueEmptyB, headerCanon);
        if (dkimCanon.EndsWith("\r\n", System.StringComparison.Ordinal))
        {
            dkimCanon = dkimCanon.Substring(0, dkimCanon.Length - 2);
        }
        signingInput.Append(dkimCanon);

        byte[] signedBytes = Encoding.UTF8.GetBytes(signingInput.ToString());
        byte[] signature;
        try
        {
            signature = System.Convert.FromBase64String(StripWhitespace(signatureBase64));
        }
        catch (System.FormatException)
        {
            return new DkimDetail
            {
                Result = DkimResult.PermError,
                Domain = domain,
                Selector = selector,
                Algorithm = algorithm,
                Explanation = "b= value is not valid base64.",
            };
        }

        return CheckSignature(publicKeyBase64, signedBytes, signature, hashAlg, domain, selector, algorithm);
    }

    /// <summary>The <paramref name="skip"/>-th instance (0 = bottom-most) of a
    /// header, counting upwards, or null when there are not that many.</summary>
    private static Anjal.Dkim.RawHeader? NthFromBottom(Anjal.Dkim.DkimMessage parsed, string name, int skip)
    {
        int seen = 0;
        for (int i = parsed.Headers.Count - 1; i >= 0; i--)
        {
            if (string.Equals(parsed.Headers[i].Name, name, System.StringComparison.OrdinalIgnoreCase))
            {
                if (seen == skip)
                {
                    return parsed.Headers[i];
                }
                seen++;
            }
        }
        return null;
    }

    /// <summary>
    /// Fetch the public key from the <c>selector._domainkey.domain</c> TXT
    /// record. Returns the base64-encoded SubjectPublicKeyInfo from the
    /// <c>p=</c> tag.
    /// </summary>
    private async System.Threading.Tasks.Task<string> FetchPublicKeyAsync(
        string domain,
        string selector,
        System.Threading.CancellationToken ct)
    {
        string name = $"{selector}._domainkey.{domain}";
        System.Collections.Generic.IReadOnlyList<string> records;
        try
        {
            records = await this.dns.LookupTxtAsync(name, ct).ConfigureAwait(false);
        }
        catch (Anjal.Dns.DnsException ex)
        {
            throw new DkimKeyFetchError($"DNS lookup for {name} failed: {ex.Message}", transient: true);
        }
        if (records.Count == 0)
        {
            throw new DkimKeyFetchError($"No DKIM public key at {name}.", transient: false);
        }

        // Use the first v=DKIM1 record.
        foreach (string record in records)
        {
            System.Collections.Generic.Dictionary<string, string> tags = ParseTags(record);
            if (tags.TryGetValue("v", out string? v) && !string.Equals(v, "DKIM1", System.StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (tags.TryGetValue("p", out string? p))
            {
                if (string.IsNullOrEmpty(p))
                {
                    throw new DkimKeyFetchError($"DKIM key at {name} is revoked (empty p=).", transient: false);
                }
                return p;
            }
        }
        throw new DkimKeyFetchError($"No valid DKIM v=DKIM1 record at {name}.", transient: false);
    }

    /// <summary>
    /// Parse a DKIM tag-list (semicolon-separated key=value pairs).
    /// </summary>
    internal static System.Collections.Generic.Dictionary<string, string> ParseTags(string raw)
    {
        var result = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        string[] parts = raw.Split(';');
        foreach (string p in parts)
        {
            string trimmed = p.Trim();
            if (trimmed.Length == 0) continue;
            int eq = trimmed.IndexOf('=', System.StringComparison.Ordinal);
            if (eq < 0) continue;
            string key = trimmed.Substring(0, eq).Trim();
            string val = trimmed.Substring(eq + 1).Trim();
            result[key] = val;
        }
        return result;
    }

    /// <summary>
    /// Return the DKIM-Signature value with the b= tag's value (but not the
    /// tag itself) emptied. Per RFC 6376 §3.7 step 5b.
    /// </summary>
    private static string RemoveBTagValue(string raw)
    {
        // Find "b=" (not "bh=") and replace everything after it up to the
        // next ";" or end-of-string with empty.
        int idx = 0;
        while (idx < raw.Length)
        {
            int b = raw.IndexOf('b', idx);
            if (b < 0) break;
            // Skip "bh=".
            if (b + 1 < raw.Length && raw[b + 1] == 'h')
            {
                idx = b + 2;
                continue;
            }
            // Skip if not preceded by start, ';', or whitespace (avoid "abc=").
            if (b > 0)
            {
                char prev = raw[b - 1];
                if (prev != ';' && prev != ' ' && prev != '\t' && prev != '\r' && prev != '\n')
                {
                    idx = b + 1;
                    continue;
                }
            }
            if (b + 1 < raw.Length && raw[b + 1] == '=')
            {
                int after = b + 2;
                int end = raw.IndexOf(';', after);
                string keep = end < 0 ? string.Empty : raw.Substring(end);
                return string.Concat(raw.AsSpan(0, after), keep);
            }
            idx = b + 1;
        }
        return raw;
    }

    private static string StripWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c is not ' ' and not '\t' and not '\r' and not '\n')
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private sealed class DkimKeyFetchError : System.Exception
    {
        public bool Transient { get; }
        public DkimKeyFetchError(string message, bool transient) : base(message)
        {
            this.Transient = transient;
        }
    }

    /// <summary>
    /// Verify an RSA signature against a published key, applying the RFC 8301
    /// minimum key size. Separated from the DNS fetch so it can be exercised
    /// directly.
    /// </summary>
    internal static DkimDetail CheckSignature(string publicKeyBase64, byte[] signedBytes, byte[] signature, HashAlgorithmName hashAlg, string domain, string selector, string algorithm)
    {
        try
        {
            using RSA rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(System.Convert.FromBase64String(StripWhitespace(publicKeyBase64)), out _);

            // RFC 8301 section 3.2: keys shorter than 1024 bits MUST NOT be
            // treated as valid. A 512-bit key can be factored cheaply.
            if (rsa.KeySize < MinimumKeyBits)
            {
                return new DkimDetail
                {
                    Result = DkimResult.PermError,
                    Domain = domain,
                    Selector = selector,
                    Algorithm = algorithm,
                    Explanation = $"Key at {selector}._domainkey.{domain} is {rsa.KeySize} bits; at least {MinimumKeyBits} are required (RFC 8301).",
                };
            }

            bool valid = rsa.VerifyData(signedBytes, signature, hashAlg, RSASignaturePadding.Pkcs1);
            return new DkimDetail
            {
                Result = valid ? DkimResult.Pass : DkimResult.Fail,
                Domain = domain,
                Selector = selector,
                Algorithm = algorithm,
                Explanation = valid
                    ? $"Signature verified against {selector}._domainkey.{domain}."
                    : $"Signature did not match public key at {selector}._domainkey.{domain}.",
            };
        }
        catch (System.Exception ex) when (ex is System.FormatException || ex is CryptographicException)
        {
            return new DkimDetail
            {
                Result = DkimResult.PermError,
                Domain = domain,
                Selector = selector,
                Algorithm = algorithm,
                Explanation = $"Public key import failed: {ex.Message}",
            };
        }
    }
}
