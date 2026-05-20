namespace Anjal.Smtp;

/// <summary>
/// Context for a delivery attempt. Captures the SMTP envelope and the
/// fully-received DATA payload. The receiver hands one of these to its
/// configured <see cref="IMessageSink"/> after each successful DATA.
/// </summary>
public sealed class DeliveryContext
{
    /// <summary>The MAIL FROM address from the SMTP envelope (without angle brackets).</summary>
    public string EnvelopeFrom { get; init; } = string.Empty;

    /// <summary>The RCPT TO addresses from the SMTP envelope (without angle brackets).</summary>
    public System.Collections.Generic.IReadOnlyList<string> EnvelopeTo { get; init; } = System.Array.Empty<string>();

    /// <summary>The full DATA payload as received over the wire (CRLF preserved, dot-unstuffed).</summary>
    public byte[] RawBytes { get; init; } = System.Array.Empty<byte>();

    /// <summary>IP address of the connected client, in dotted form.</summary>
    public string RemoteAddress { get; init; } = string.Empty;

    /// <summary>The EHLO/HELO hostname the client claimed.</summary>
    public string ClientHostName { get; init; } = string.Empty;

    /// <summary>
    /// Authentication detail (SPF/DKIM/DMARC verdicts) if an authenticator
    /// was configured. Null if inbound auth is disabled. The concrete type
    /// is <c>Anjal.Auth.AuthenticationResults</c> when populated.
    /// </summary>
    public object? AuthResults { get; init; }
}

/// <summary>
/// Outcome the SMTP receiver should report back to the connected client
/// after DATA. Influences the SMTP reply code emitted on the wire.
/// </summary>
public enum DeliveryOutcome
{
    /// <summary>Message was accepted and persisted. SMTP 250.</summary>
    Accepted = 0,

    /// <summary>Permanent failure - reject (550). Useful for unknown recipients
    /// that managed to slip past RCPT validation.</summary>
    PermanentFailure = 1,

    /// <summary>Transient failure - the sender should retry (450). Useful when
    /// the database is briefly unavailable or a webhook is unreachable.</summary>
    TransientFailure = 2,
}

/// <summary>
/// Result of a delivery, including the outcome and human-readable text
/// that becomes part of the SMTP reply.
/// </summary>
public sealed class DeliveryResult
{
    /// <summary>The outcome.</summary>
    public DeliveryOutcome Outcome { get; init; }

    /// <summary>Reply text to send back to the SMTP client.</summary>
    public string ReplyText { get; init; } = "OK";
}

/// <summary>
/// Sink that consumes accepted SMTP messages. Implementations route to the
/// store, fire webhooks, log, etc. The composition root in the host project
/// wires a concrete sink (typically a delegate to MIME-parse, store-save,
/// webhook-fire) into the receiver.
/// </summary>
public interface IMessageSink
{
    /// <summary>
    /// Accept and process a delivered message. Returning a transient failure
    /// causes the SMTP client to be told to retry; returning a permanent
    /// failure causes the message to be rejected with 550.
    /// </summary>
    /// <param name="ctx">The delivery context.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<DeliveryResult> DeliverAsync(DeliveryContext ctx, System.Threading.CancellationToken ct = default);
}

/// <summary>
/// Optional pluggable authenticator for inbound mail. Runs after DATA
/// but before the message is handed to the sink. Lets the server enforce
/// SPF/DKIM/DMARC verdicts at the SMTP layer (e.g. reject 550 when DMARC
/// says p=reject).
/// </summary>
public interface IInboundAuthenticator
{
    /// <summary>
    /// Authenticate an inbound message. Returns a structured result
    /// containing per-verifier verdicts and the formatted
    /// <c>Authentication-Results</c> header value. The implementation
    /// MUST NOT throw - it should return TempError/PermError verdicts
    /// instead of raising.
    /// </summary>
    /// <param name="remoteAddress">Connecting client's IP as a string.</param>
    /// <param name="envelopeFrom">SMTP MAIL FROM value.</param>
    /// <param name="messageBytes">Full RFC 5322 message bytes.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Authentication result for the message.</returns>
    System.Threading.Tasks.Task<InboundAuthResult> AuthenticateAsync(
        string remoteAddress,
        string envelopeFrom,
        byte[] messageBytes,
        System.Threading.CancellationToken ct = default);
}

/// <summary>
/// Outcome of inbound authentication. Carries the formatted
/// <c>Authentication-Results</c> header value to prepend to the message,
/// and a flag indicating whether DMARC policy requires this server to
/// reject the message.
/// </summary>
public sealed class InboundAuthResult
{
    /// <summary>
    /// The <c>Authentication-Results</c> header value to prepend (no
    /// field-name prefix, no terminating CRLF).
    /// </summary>
    public string HeaderValue { get; init; } = string.Empty;

    /// <summary>
    /// True if DMARC published <c>p=reject</c> and authentication failed.
    /// When the server is configured to enforce DMARC, set this to refuse
    /// the message with SMTP 550 before sink dispatch.
    /// </summary>
    public bool ShouldReject { get; init; }

    /// <summary>
    /// Opaque structured data exposed to the sink. The Smtp layer doesn't
    /// interpret this; it's passed through to <see cref="DeliveryContext.AuthResults"/>
    /// so the webhook payload can include full per-verifier detail.
    /// </summary>
    public object? Detail { get; init; }

    /// <summary>
    /// SMTP reply text to send with the 550 when <see cref="ShouldReject"/>
    /// is true. Empty falls back to a generic message.
    /// </summary>
    public string RejectReason { get; init; } = string.Empty;
}
