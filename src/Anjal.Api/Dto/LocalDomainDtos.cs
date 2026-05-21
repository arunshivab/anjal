namespace Anjal.Api.Dto;

/// <summary>
/// Request body for registering a domain as local.
/// </summary>
public sealed class LocalDomainRequest
{
    /// <summary>The domain name (lowercase recommended).</summary>
    public string Domain { get; set; } = string.Empty;
}

/// <summary>
/// Response body for a registered local domain.
/// </summary>
public sealed class LocalDomainResponse
{
    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The domain name.</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>When the row was created.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }
}
