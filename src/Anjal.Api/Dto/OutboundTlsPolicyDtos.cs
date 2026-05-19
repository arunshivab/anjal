namespace Anjal.Api.Dto;

/// <summary>
/// Request body for <c>POST /api/outbound-tls-policies</c>: create or
/// update the TLS policy for a destination domain.
/// </summary>
public sealed class OutboundTlsPolicyRequest
{
    /// <summary>Destination domain (case-insensitive), e.g. "gmail.com".</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>One of "opportunistic", "required", "disabled".</summary>
    public string Mode { get; set; } = "opportunistic";
}

/// <summary>
/// Response body for outbound TLS policy operations.
/// </summary>
public sealed class OutboundTlsPolicyResponse
{
    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The destination domain.</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>Effective TLS mode ("opportunistic", "required", "disabled").</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>When the policy was created or last updated.</summary>
    public System.DateTimeOffset UpdatedAt { get; set; }
}
