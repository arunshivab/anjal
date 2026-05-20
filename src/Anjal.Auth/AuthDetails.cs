namespace Anjal.Auth;

/// <summary>
/// Detailed result of an SPF check. Exposes which mechanism matched, the
/// number of DNS lookups used (against the 10-lookup RFC 7208 limit), and
/// the explanation string if one was provided.
/// </summary>
public sealed class SpfDetail
{
    /// <summary>The verdict.</summary>
    public SpfResult Result { get; init; }

    /// <summary>Domain that was checked (the MAIL FROM domain, or HELO if MAIL FROM is empty).</summary>
    public string Domain { get; init; } = string.Empty;

    /// <summary>Peer IP address that was checked.</summary>
    public string PeerAddress { get; init; } = string.Empty;

    /// <summary>The SPF mechanism that produced the result (e.g. <c>ip4:192.0.2.0/24</c>,
    /// <c>include:_spf.google.com</c>, <c>-all</c>). Empty if Result is None or PermError.</summary>
    public string MatchedMechanism { get; init; } = string.Empty;

    /// <summary>Number of DNS lookups performed (against the RFC 7208 limit of 10).</summary>
    public int LookupCount { get; init; }

    /// <summary>Human-readable explanation of the result, suitable for inclusion in
    /// the <c>Authentication-Results</c> header's <c>comment</c>.</summary>
    public string Explanation { get; init; } = string.Empty;
}

/// <summary>
/// Detailed result of a DKIM signature verification. Anjal verifies the
/// first DKIM-Signature header it encounters; messages with multiple
/// signatures will have only the first reported here.
/// </summary>
public sealed class DkimDetail
{
    /// <summary>The verdict.</summary>
    public DkimResult Result { get; init; }

    /// <summary>The signing domain (<c>d=</c> tag), or empty if no signature.</summary>
    public string Domain { get; init; } = string.Empty;

    /// <summary>The selector (<c>s=</c> tag), or empty if no signature.</summary>
    public string Selector { get; init; } = string.Empty;

    /// <summary>Algorithm used (e.g. <c>rsa-sha256</c>), or empty if no signature.</summary>
    public string Algorithm { get; init; } = string.Empty;

    /// <summary>Human-readable explanation suitable for the <c>Authentication-Results</c> comment.</summary>
    public string Explanation { get; init; } = string.Empty;
}

/// <summary>
/// Detailed result of a DMARC evaluation.
/// </summary>
public sealed class DmarcDetail
{
    /// <summary>The verdict.</summary>
    public DmarcResult Result { get; init; }

    /// <summary>Organizational domain from the From header that was used to
    /// look up the DMARC record.</summary>
    public string FromDomain { get; init; } = string.Empty;

    /// <summary>The published policy (<c>p=</c> tag), if a record was found.</summary>
    public DmarcPolicy Policy { get; init; }

    /// <summary>SPF alignment mode (<c>aspf=</c> tag). Default Relaxed.</summary>
    public AlignmentMode SpfAlignment { get; init; }

    /// <summary>DKIM alignment mode (<c>adkim=</c> tag). Default Relaxed.</summary>
    public AlignmentMode DkimAlignment { get; init; }

    /// <summary>True if SPF passed and the MAIL FROM domain aligned with the From domain.</summary>
    public bool SpfAligned { get; init; }

    /// <summary>True if DKIM passed and the signing domain aligned with the From domain.</summary>
    public bool DkimAligned { get; init; }

    /// <summary>The domain that was successfully aligned (empty if neither aligned).</summary>
    public string AlignedDomain { get; init; } = string.Empty;

    /// <summary>Human-readable explanation suitable for the <c>Authentication-Results</c> comment.</summary>
    public string Explanation { get; init; } = string.Empty;
}

/// <summary>
/// Aggregated authentication results for one inbound message. Produced
/// by <see cref="InboundAuthenticator"/> and exposed via the webhook payload.
/// </summary>
public sealed class AuthenticationResults
{
    /// <summary>The hostname of the verifying Anjal instance (used as the
    /// <c>authserv-id</c> in the <c>Authentication-Results</c> header).</summary>
    public string ServingHost { get; init; } = string.Empty;

    /// <summary>SPF result and detail.</summary>
    public SpfDetail Spf { get; init; } = new();

    /// <summary>DKIM result and detail.</summary>
    public DkimDetail Dkim { get; init; } = new();

    /// <summary>DMARC result and detail.</summary>
    public DmarcDetail Dmarc { get; init; } = new();

    /// <summary>The full RFC 8601 <c>Authentication-Results</c> header value
    /// to prepend to the message (without the field name or terminating CRLF).</summary>
    public string HeaderValue { get; init; } = string.Empty;
}
