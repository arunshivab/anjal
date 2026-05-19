namespace Anjal.Store;

/// <summary>
/// TLS handling mode for an outbound send to a particular destination.
/// </summary>
public enum TlsMode
{
    /// <summary>Try STARTTLS, fall back to plaintext if the server does not advertise it.</summary>
    Opportunistic = 0,

    /// <summary>STARTTLS is mandatory. If the server does not advertise it, the send fails (transient).</summary>
    Required = 1,

    /// <summary>Do not use TLS even if advertised. Useful for testing and trusted local relays.</summary>
    Disabled = 2,
}

/// <summary>
/// TLS policy for outbound sends to a specific destination domain. If no
/// policy exists for a domain, the configured default policy is used.
/// </summary>
public sealed class OutboundTlsPolicy
{
    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>
    /// Destination domain this policy applies to (case-insensitive). For
    /// example, "gmail.com", "partner-hospital.example".
    /// In relay mode, the lookup is against the relay's hostname.
    /// </summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>The TLS mode to apply when sending to this domain.</summary>
    public TlsMode Mode { get; set; }

    /// <summary>When this policy was created or last updated.</summary>
    public System.DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Status of a queued outbound message.
/// </summary>
public enum OutboundStatus
{
    /// <summary>Waiting to be sent (or retried after a transient failure).</summary>
    Pending = 0,

    /// <summary>Currently leased by a worker for a send attempt.</summary>
    Sending = 1,

    /// <summary>Successfully accepted by the destination.</summary>
    Sent = 2,

    /// <summary>Bounced (permanent failure or maximum retries reached).</summary>
    Failed = 3,
}

/// <summary>
/// A queued outbound message. Created by the API layer or by a webhook
/// auto-reply rule; consumed by an <c>OutboundWorker</c> background task
/// that runs send attempts with exponential backoff.
/// </summary>
public sealed class OutboundMessage
{
    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The SMTP envelope sender (no angle brackets).</summary>
    public string EnvelopeFrom { get; set; } = string.Empty;

    /// <summary>The SMTP envelope recipient (no angle brackets). One row per recipient.</summary>
    public string EnvelopeTo { get; set; } = string.Empty;

    /// <summary>The raw RFC 5322 message bytes to send in the DATA phase.</summary>
    public byte[] RawBytes { get; set; } = System.Array.Empty<byte>();

    /// <summary>Current status.</summary>
    public OutboundStatus Status { get; set; }

    /// <summary>Number of send attempts made so far.</summary>
    public int Attempts { get; set; }

    /// <summary>Earliest time the next send attempt should be tried.</summary>
    public System.DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>Time the message was enqueued.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Cutoff after which the message should be permanently failed regardless
    /// of remaining retry budget. Default is 24 hours after creation; the
    /// caller can override per-message.
    /// </summary>
    public System.DateTimeOffset GiveUpAt { get; set; }

    /// <summary>The reply text of the most recent attempt (success or failure).</summary>
    public string LastError { get; set; } = string.Empty;
}

/// <summary>
/// A stored inbound message. The <see cref="Id"/> is assigned by the store
/// on save; callers should ignore the value they pass in.
/// </summary>
public sealed class InboundMessage
{
    /// <summary>Identifier assigned by the store. <see cref="System.Guid.Empty"/> if not yet persisted.</summary>
    public System.Guid Id { get; set; }

    /// <summary>SMTP envelope MAIL FROM. May differ from the Message header From.</summary>
    public string EnvelopeFrom { get; set; } = string.Empty;

    /// <summary>SMTP envelope RCPT TO. Always exactly one address per stored row.</summary>
    public string EnvelopeTo { get; set; } = string.Empty;

    /// <summary>The Message-ID header value with angle brackets stripped, or empty.</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>The Subject header, decoded if RFC 2047 encoded.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Time the message was received and stored.</summary>
    public System.DateTimeOffset ReceivedAt { get; set; }

    /// <summary>The raw RFC 5322 message bytes as received over SMTP.</summary>
    public byte[] RawBytes { get; set; } = System.Array.Empty<byte>();

    /// <summary>Resolved local-part (left of "@", with any "+tag" stripped).</summary>
    public string LocalPart { get; set; } = string.Empty;

    /// <summary>The "+tag" portion of the recipient if present, otherwise empty.</summary>
    public string Tag { get; set; } = string.Empty;
}

/// <summary>
/// A routing rule stored in the address table. Maps an inbound local-part
/// (the left side of an "@" address) to a webhook URL that the dispatcher
/// will POST messages to.
/// </summary>
public sealed class RoutingRule
{
    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The local-part this rule matches, case-insensitive.</summary>
    public string LocalPart { get; set; } = string.Empty;

    /// <summary>HTTP(S) URL to POST the parsed message to.</summary>
    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>
    /// Secret used for HMAC-SHA256 signing of webhook payloads.
    /// Stored hex-encoded.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>When this rule was created.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A time-bounded authorisation for a specific "+tag" against a routing rule.
/// Lets SIGMA grant a patient permission to send to <c>reports+X7Y9@host</c>
/// for case 18472, expiring in 30 days.
/// </summary>
public sealed class TagGrant
{
    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The local-part this grant relates to.</summary>
    public string LocalPart { get; set; } = string.Empty;

    /// <summary>The "+tag" string this grant authorises.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>Application-supplied correlation key (e.g. a case ID).</summary>
    public string CorrelationKey { get; set; } = string.Empty;

    /// <summary>When the grant was issued.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the grant expires. Messages arriving after are rejected.</summary>
    public System.DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// Outcome of a webhook delivery attempt. Persisted so that operators can
/// inspect why a message didn't reach the destination application.
/// </summary>
public sealed class WebhookDelivery
{
    /// <summary>Identifier assigned by the store.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The inbound message this delivery relates to.</summary>
    public System.Guid InboundMessageId { get; set; }

    /// <summary>Webhook URL that was called.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>HTTP status code returned by the webhook, or 0 if no response.</summary>
    public int StatusCode { get; set; }

    /// <summary>Time the delivery was attempted.</summary>
    public System.DateTimeOffset AttemptedAt { get; set; }

    /// <summary>Error message if the call failed before returning a status.</summary>
    public string ErrorMessage { get; set; } = string.Empty;
}
