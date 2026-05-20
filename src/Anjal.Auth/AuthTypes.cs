namespace Anjal.Auth;

/// <summary>
/// Inbound mail authentication. Implements:
/// <list type="bullet">
///   <item>SPF verification per RFC 7208</item>
///   <item>DKIM signature verification per RFC 6376</item>
///   <item>DMARC policy evaluation per RFC 7489</item>
///   <item><c>Authentication-Results</c> header formatting per RFC 8601</item>
/// </list>
/// All DNS lookups go through <see cref="Anjal.Dns.DnsResolver"/>. Zero
/// external NuGet dependencies; verification uses the BCL's
/// <c>System.Security.Cryptography.RSA</c>.
/// </summary>
public static class ModuleInfo
{
    /// <summary>Module name as it appears in logs and diagnostics.</summary>
    public const string Name = "Anjal.Auth";
}

/// <summary>
/// SPF check result per RFC 7208 section 2.6.
/// </summary>
public enum SpfResult
{
    /// <summary>No SPF record exists for the domain, or no check has been
    /// performed. This is the default value for an uninitialized result -
    /// uninitialized means "no verdict computed", not "passed".</summary>
    None = 0,

    /// <summary>Domain explicitly authorizes the host.</summary>
    Pass = 1,

    /// <summary>Domain explicitly does not authorize the host. Receivers SHOULD reject.</summary>
    Fail = 2,

    /// <summary>Domain weakly does not authorize (intended to be advisory).</summary>
    SoftFail = 3,

    /// <summary>Domain makes no assertion about the host.</summary>
    Neutral = 4,

    /// <summary>SPF record is malformed (cached as a permanent error).</summary>
    PermError = 5,

    /// <summary>DNS lookup failure or transient processing error.</summary>
    TempError = 6,
}

/// <summary>
/// DKIM verification result per RFC 6376 section 3.9.
/// </summary>
public enum DkimResult
{
    /// <summary>No DKIM-Signature header was present, or no check has been
    /// performed. This is the default for an uninitialized result.</summary>
    None = 0,

    /// <summary>Signature successfully verified.</summary>
    Pass = 1,

    /// <summary>Signature was syntactically valid but failed cryptographic verification.</summary>
    Fail = 2,

    /// <summary>Signature had syntactic problems that prevented verification.</summary>
    PermError = 3,

    /// <summary>Verification was halted by a transient problem (e.g. DNS timeout).</summary>
    TempError = 4,
}

/// <summary>
/// DMARC evaluation result per RFC 7489 section 6.6.
/// </summary>
public enum DmarcResult
{
    /// <summary>No DMARC record exists for the domain, or no check has
    /// been performed. This is the default for an uninitialized result.</summary>
    None = 0,

    /// <summary>At least one of SPF or DKIM passed and was aligned.</summary>
    Pass = 1,

    /// <summary>Both SPF and DKIM either failed or weren't aligned.</summary>
    Fail = 2,

    /// <summary>DMARC record was malformed.</summary>
    PermError = 3,

    /// <summary>DNS lookup failure or transient processing error.</summary>
    TempError = 4,
}

/// <summary>
/// DMARC policy disposition per RFC 7489 section 6.3.
/// </summary>
public enum DmarcPolicy
{
    /// <summary>Take no action.</summary>
    None = 0,

    /// <summary>Treat as suspicious.</summary>
    Quarantine = 1,

    /// <summary>Reject the message.</summary>
    Reject = 2,
}

/// <summary>
/// DMARC alignment mode per RFC 7489 section 3.1.
/// </summary>
public enum AlignmentMode
{
    /// <summary>Organizational domain match is sufficient (default).</summary>
    Relaxed = 0,

    /// <summary>Full domain match required.</summary>
    Strict = 1,
}
