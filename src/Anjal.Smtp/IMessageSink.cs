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
