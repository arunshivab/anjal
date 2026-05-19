namespace Anjal.Api.Dto;

/// <summary>
/// Request body for <c>POST /api/dkim-keys</c>: upload or rotate a DKIM
/// signing key for a sender domain.
/// </summary>
public sealed class DkimKeyRequest
{
    /// <summary>Sender domain (case-insensitive). For example "mail.lipihis.in".</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>The selector (becomes the <c>s=</c> tag in the signature
    /// and part of the DNS TXT record name).</summary>
    public string Selector { get; set; } = string.Empty;

    /// <summary>RSA private key in PKCS#8 PEM form
    /// (<c>-----BEGIN PRIVATE KEY-----</c> ... <c>-----END PRIVATE KEY-----</c>).</summary>
    public string PrivateKeyPem { get; set; } = string.Empty;
}

/// <summary>
/// Response body for DKIM key operations. The private key is NEVER
/// returned in API responses - only the metadata.
/// </summary>
public sealed class DkimKeyResponse
{
    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The sender domain.</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>The selector.</summary>
    public string Selector { get; set; } = string.Empty;

    /// <summary>When the key was created or last updated.</summary>
    public System.DateTimeOffset UpdatedAt { get; set; }
}
