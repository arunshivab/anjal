namespace Anjal.Api.Dto;

/// <summary>Response body for <c>GET /api/acme</c>.</summary>
public sealed class AcmeStatusResponse
{
    /// <summary>Certificate directory on disk.</summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>Whether a certificate is stored and loadable.</summary>
    public bool HasCertificate { get; set; }

    /// <summary>Certificate subject.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Certificate NotBefore (UTC).</summary>
    public System.DateTimeOffset? NotBefore { get; set; }

    /// <summary>Certificate NotAfter (UTC).</summary>
    public System.DateTimeOffset? NotAfter { get; set; }

    /// <summary>Whole days until expiry (negative if expired).</summary>
    public int? DaysRemaining { get; set; }

    /// <summary>Domains recorded at issuance.</summary>
    public string[] Domains { get; set; } = System.Array.Empty<string>();

    /// <summary>Key type recorded at issuance.</summary>
    public string KeyType { get; set; } = string.Empty;

    /// <summary>When the certificate was issued (UTC).</summary>
    public System.DateTimeOffset? IssuedAt { get; set; }

    /// <summary>ACME directory URL in use.</summary>
    public string AcmeDirectoryUrl { get; set; } = string.Empty;

    /// <summary>Whether a renew-now request is waiting to be picked up.</summary>
    public bool RenewalPending { get; set; }

    /// <summary>When the renewal service last attempted issuance (UTC).</summary>
    public System.DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>Whether that attempt succeeded.</summary>
    public bool LastAttemptSucceeded { get; set; }

    /// <summary>Error text from the last failed attempt.</summary>
    public string LastError { get; set; } = string.Empty;

    /// <summary>Consecutive failures since the last success.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>When the renewal service will next check (UTC).</summary>
    public System.DateTimeOffset? NextCheckAt { get; set; }

    /// <summary>When the renewal service last wrote its status (UTC); stale means it is not running.</summary>
    public System.DateTimeOffset? StatusUpdatedAt { get; set; }
}

/// <summary>Response body for <c>POST /api/acme/renew</c>.</summary>
public sealed class AcmeRenewResponse
{
    /// <summary>Always true on success.</summary>
    public bool Requested { get; set; }

    /// <summary>Path of the marker file the renewal service will consume.</summary>
    public string Marker { get; set; } = string.Empty;
}
