namespace Anjal.Api.Dto;

/// <summary>
/// Request body for <c>POST /api/routing-rules</c>: create or update a
/// routing rule for a local-part.
/// </summary>
public sealed class RoutingRuleRequest
{
    /// <summary>The local-part this rule matches, case-insensitive.</summary>
    public string LocalPart { get; set; } = string.Empty;

    /// <summary>HTTP(S) URL Anjal will POST inbound messages to.</summary>
    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>
    /// Hex-encoded shared secret used for HMAC-SHA256 signing of webhook
    /// payloads. The caller is responsible for keeping this private. Typical
    /// length is 32 bytes (64 hex chars).
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;
}

/// <summary>
/// Response body for routing-rule operations. The webhook secret is NEVER
/// echoed back, even on the creating call - if the caller needs it, they
/// must save it client-side at the moment of creation.
/// </summary>
public sealed class RoutingRuleResponse
{
    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The local-part.</summary>
    public string LocalPart { get; set; } = string.Empty;

    /// <summary>The webhook URL.</summary>
    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>When this rule was created.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Request body for <c>POST /api/tag-grants</c>: issue a time-bounded
/// authorisation for <c>localPart+tag</c>.
/// </summary>
public sealed class TagGrantRequest
{
    /// <summary>The local-part the grant applies to.</summary>
    public string LocalPart { get; set; } = string.Empty;

    /// <summary>The "+tag" string this grant authorises.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>Application-supplied correlation key (e.g. a case ID).</summary>
    public string CorrelationKey { get; set; } = string.Empty;

    /// <summary>Time-to-live in seconds. The grant expires this far in the future.</summary>
    public int TtlSeconds { get; set; }
}

/// <summary>
/// Response body for tag-grant operations.
/// </summary>
public sealed class TagGrantResponse
{
    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The local-part.</summary>
    public string LocalPart { get; set; } = string.Empty;

    /// <summary>The "+tag" string.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>Correlation key.</summary>
    public string CorrelationKey { get; set; } = string.Empty;

    /// <summary>When the grant was issued.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the grant expires.</summary>
    public System.DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// Request body for <c>POST /api/outbound</c>: enqueue an outbound message.
/// Either <see cref="RawBytesBase64"/> must be supplied (a pre-built MIME
/// message) OR the <see cref="Subject"/> and <see cref="BodyText"/> fields
/// (in which case Anjal builds a simple text/plain message).
/// </summary>
public sealed class OutboundRequest
{
    /// <summary>The SMTP envelope sender (no angle brackets).</summary>
    public string EnvelopeFrom { get; set; } = string.Empty;

    /// <summary>The SMTP envelope recipient (no angle brackets).</summary>
    public string EnvelopeTo { get; set; } = string.Empty;

    /// <summary>
    /// Base64-encoded RFC 5322 message bytes. Mutually exclusive with the
    /// structured fields below.
    /// </summary>
    public string RawBytesBase64 { get; set; } = string.Empty;

    /// <summary>The From header. Used when RawBytesBase64 is empty.</summary>
    public string FromHeader { get; set; } = string.Empty;

    /// <summary>The To header. Used when RawBytesBase64 is empty.</summary>
    public string ToHeader { get; set; } = string.Empty;

    /// <summary>The Subject header. Used when RawBytesBase64 is empty.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>The plain-text body. Used when RawBytesBase64 is empty.</summary>
    public string BodyText { get; set; } = string.Empty;

    /// <summary>
    /// Optional override for the give-up deadline in hours from now.
    /// Default is 24. Useful for less-time-sensitive messages.
    /// </summary>
    public int GiveUpHours { get; set; }
}

/// <summary>
/// Response body for an outbound message - both the immediate enqueue
/// reply and the status-fetch endpoint.
/// </summary>
public sealed class OutboundResponse
{
    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>SMTP envelope sender.</summary>
    public string EnvelopeFrom { get; set; } = string.Empty;

    /// <summary>SMTP envelope recipient.</summary>
    public string EnvelopeTo { get; set; } = string.Empty;

    /// <summary>Current status as a string ("Pending", "Sending", "Sent", "Failed").</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Number of send attempts so far.</summary>
    public int Attempts { get; set; }

    /// <summary>When the message was enqueued.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }

    /// <summary>Earliest time of the next send attempt.</summary>
    public System.DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>Give-up deadline.</summary>
    public System.DateTimeOffset GiveUpAt { get; set; }

    /// <summary>Most recent reply text or error.</summary>
    public string LastError { get; set; } = string.Empty;
}

/// <summary>
/// Response body for <c>GET /api/inbound/{id}</c>.
/// </summary>
public sealed class InboundResponse
{
    /// <summary>The message identifier.</summary>
    public System.Guid Id { get; set; }

    /// <summary>SMTP envelope sender.</summary>
    public string EnvelopeFrom { get; set; } = string.Empty;

    /// <summary>SMTP envelope recipient.</summary>
    public string EnvelopeTo { get; set; } = string.Empty;

    /// <summary>Decoded local-part.</summary>
    public string LocalPart { get; set; } = string.Empty;

    /// <summary>Decoded tag.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>Decoded Subject header.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Message-ID header.</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>When the message was received.</summary>
    public System.DateTimeOffset ReceivedAt { get; set; }

    /// <summary>Raw MIME bytes, base64-encoded.</summary>
    public string RawBytesBase64 { get; set; } = string.Empty;
}

/// <summary>
/// Uniform error body returned with non-2xx responses.
/// </summary>
public sealed class ErrorResponse
{
    /// <summary>Short machine-readable error code.</summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>Human-readable message.</summary>
    public string Message { get; set; } = string.Empty;
}
