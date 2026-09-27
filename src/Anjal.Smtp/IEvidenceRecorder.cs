namespace Anjal.Smtp;

/// <summary>
/// Keeps the original of every incoming message exactly as received
/// (v1.0.0-rc.8, ANJAL-DES-01). The SMTP session calls it before delivery;
/// if it cannot record, the message is deferred - nothing is accepted
/// without its evidence (SPEC-08 R-08).
/// </summary>
public interface IEvidenceRecorder
{
    /// <summary>Store the original; returns its evidence id. Throws when it cannot be stored.</summary>
    /// <param name="evidence">The message as received.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The evidence id.</returns>
    Task<System.Guid> RecordInboundAsync(InboundEvidence evidence, CancellationToken ct = default);

    /// <summary>Record whether the message was accepted; a copy of mail not accepted is purged.</summary>
    /// <param name="id">The evidence id.</param>
    /// <param name="accepted">Whether the mailboxes accepted it.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task CompleteInboundAsync(System.Guid id, bool accepted, CancellationToken ct = default);
}

/// <summary>An incoming message exactly as received, with how it arrived.</summary>
public sealed class InboundEvidence
{
    /// <summary>The bytes after SMTP framing (dot-stuffing) was removed and before any change.</summary>
    public byte[] RawBytes { get; init; } = System.Array.Empty<byte>();

    /// <summary>The envelope sender.</summary>
    public string EnvelopeFrom { get; init; } = string.Empty;

    /// <summary>The envelope recipients.</summary>
    public IReadOnlyList<string> EnvelopeTo { get; init; } = System.Array.Empty<string>();

    /// <summary>The client's address.</summary>
    public string RemoteAddress { get; init; } = string.Empty;

    /// <summary>The name the client greeted with.</summary>
    public string ClientHostName { get; init; } = string.Empty;

    /// <summary>TLS version and cipher, or null.</summary>
    public string? TransportTls { get; init; }

    /// <summary>The authenticated user on the submission port, or null.</summary>
    public string? AuthenticatedUser { get; init; }

    /// <summary>When the message was received.</summary>
    public System.DateTimeOffset ReceivedAt { get; init; }
}
