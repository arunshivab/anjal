namespace Anjal.Dkim;

/// <summary>
/// RFC 6376 header canonicalization algorithm.
/// </summary>
public enum HeaderCanonicalization
{
    /// <summary>Headers are reproduced verbatim. Fragile - any mailer that
    /// re-folds a long header will break the signature.</summary>
    Simple = 0,

    /// <summary>Header names are lowercased, runs of internal whitespace
    /// collapsed to a single SP, trailing whitespace stripped, folded
    /// continuations unfolded. The default - tolerant of normal mail
    /// transit modifications.</summary>
    Relaxed = 1,
}

/// <summary>
/// RFC 6376 body canonicalization algorithm.
/// </summary>
public enum BodyCanonicalization
{
    /// <summary>Body is reproduced verbatim except trailing empty lines.</summary>
    Simple = 0,

    /// <summary>Trailing whitespace stripped from each line; runs of
    /// internal whitespace collapsed to a single SP; trailing empty
    /// lines removed. The default.</summary>
    Relaxed = 1,
}

/// <summary>
/// A DKIM signing key bound to a specific domain and selector. The
/// selector becomes part of the DNS TXT record name:
/// <c>selector._domainkey.domain</c>.
/// </summary>
public sealed class DkimKey
{
    /// <summary>The sender domain this key signs for, e.g. "mail.lipihis.in".
    /// Used to match against the From header's domain.</summary>
    public string Domain { get; init; } = string.Empty;

    /// <summary>The selector, e.g. "default" or "2026a". Becomes part of
    /// the DNS record name and the <c>s=</c> tag in the signature.</summary>
    public string Selector { get; init; } = string.Empty;

    /// <summary>RSA private key in PKCS#8 PEM form
    /// (<c>-----BEGIN PRIVATE KEY-----</c> ... <c>-----END PRIVATE KEY-----</c>).</summary>
    public string PrivateKeyPem { get; init; } = string.Empty;
}

/// <summary>
/// Configuration controlling how messages are signed.
/// </summary>
public sealed class DkimSigningOptions
{
    /// <summary>Header canonicalization. Default: Relaxed.</summary>
    public HeaderCanonicalization HeaderCanon { get; init; } = HeaderCanonicalization.Relaxed;

    /// <summary>Body canonicalization. Default: Relaxed.</summary>
    public BodyCanonicalization BodyCanon { get; init; } = BodyCanonicalization.Relaxed;

    /// <summary>
    /// Which headers to include in the signature, in order. The
    /// <c>From</c> header is required by RFC 6376 and is always included
    /// even if omitted here. Defaults cover the headers that matter for
    /// authenticity in transactional mail.
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<string> SignedHeaders { get; init; } = new[]
    {
        "From", "To", "Subject", "Date", "Message-ID", "MIME-Version", "Content-Type",
    };

    /// <summary>When true, the <c>l=</c> body-length tag is included.
    /// Recommended OFF because <c>l=</c> permits content to be appended
    /// to the body without breaking the signature. Default: false.</summary>
    public bool IncludeBodyLength { get; init; }
}
