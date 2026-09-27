namespace Anjal.Smtp;

/// <summary>
/// One outbound delivery instruction: a message and the recipients it
/// should be sent to. Used by <see cref="IMailSender"/>.
/// </summary>
public sealed class OutboundDelivery
{
    /// <summary>The MAIL FROM address (no angle brackets).</summary>
    public string EnvelopeFrom { get; init; } = string.Empty;

    /// <summary>The RCPT TO addresses (no angle brackets).</summary>
    public System.Collections.Generic.IReadOnlyList<string> EnvelopeTo { get; init; } = System.Array.Empty<string>();

    /// <summary>The raw RFC 5322 message bytes to send in the DATA phase.</summary>
    public byte[] RawBytes { get; init; } = System.Array.Empty<byte>();
}

/// <summary>
/// Outcome classification for an outbound attempt.
/// </summary>
public enum SendOutcome
{
    /// <summary>All recipients accepted. No retry.</summary>
    Sent = 0,

    /// <summary>Transient failure (4xx, connection error, timeout). Retry later.</summary>
    TransientFailure = 1,

    /// <summary>Permanent failure (5xx). Do not retry; the message has bounced.</summary>
    PermanentFailure = 2,
}

/// <summary>
/// Result of an outbound send attempt.
/// </summary>
public sealed class SendResult
{
    /// <summary>The outcome classification.</summary>
    public SendOutcome Outcome { get; init; }

    /// <summary>SMTP reply code from the remote server (250 on success), or 0 if no reply.</summary>
    public int ReplyCode { get; init; }

    /// <summary>Description of the result. Empty on plain success.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>The server this attempt spoke to (v1.0.0-rc.8), or empty when none was reached.</summary>
    public string RemoteHost { get; init; } = string.Empty;

    /// <summary>TLS version and cipher negotiated with it, or null when the attempt was not encrypted.</summary>
    public string? TransportTls { get; init; }

    /// <summary>A copy that also names the server spoken to and the TLS used.</summary>
    /// <param name="remoteHost">The server.</param>
    /// <param name="transportTls">The TLS, or null.</param>
    /// <returns>The copy.</returns>
    public SendResult WithRoute(string remoteHost, string? transportTls) => new()
    {
        Outcome = this.Outcome,
        ReplyCode = this.ReplyCode,
        Message = this.Message,
        RemoteHost = remoteHost,
        TransportTls = transportTls,
    };
}

/// <summary>What an attempt learned about its connection, for <see cref="SendResult.WithRoute"/>.</summary>
internal sealed class RouteNote
{
    /// <summary>The TLS negotiated, once STARTTLS succeeded.</summary>
    public string? Tls { get; set; }
}

/// <summary>
/// Sends outbound mail. Two implementations are provided:
/// <list type="bullet">
///   <item><see cref="DirectMailSender"/> - looks up the destination
///   domain's MX records and connects directly.</item>
///   <item><see cref="RelayMailSender"/> - sends every message to a single
///   configured upstream SMTP relay (useful when port 25 is blocked).</item>
/// </list>
/// </summary>
public interface IMailSender
{
    /// <summary>
    /// Attempt to deliver one message. The delivery context's
    /// <see cref="OutboundDelivery.EnvelopeTo"/> list should normally contain
    /// recipients in a single destination domain - the caller groups by domain
    /// before invoking this method.
    /// </summary>
    /// <param name="delivery">The message and recipients.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<SendResult> SendAsync(
        OutboundDelivery delivery,
        System.Threading.CancellationToken ct = default);
}
