namespace Anjal.Store;

/// <summary>
/// One evidence copy (v1.0.0-rc.8, ANJAL-DES-01): the exact bytes of a message
/// as received or sent, with its SHA-256. The file is written once and never changed.
/// </summary>
public sealed class EvidenceRow
{
    /// <summary>Incoming mail.</summary>
    public const string In = "in";

    /// <summary>Outgoing mail.</summary>
    public const string Out = "out";

    /// <summary>The id; also the file name.</summary>
    public System.Guid Id { get; set; }

    /// <summary><see cref="In"/> or <see cref="Out"/>.</summary>
    public string Direction { get; set; } = In;

    /// <summary>When the bytes were captured (UTC).</summary>
    public System.DateTimeOffset CapturedAt { get; set; }

    /// <summary>The envelope sender.</summary>
    public string EnvelopeFrom { get; set; } = string.Empty;

    /// <summary>The envelope recipients.</summary>
    public IReadOnlyList<string> EnvelopeTo { get; set; } = System.Array.Empty<string>();

    /// <summary>The connecting client's address (incoming).</summary>
    public string RemoteAddress { get; set; } = string.Empty;

    /// <summary>The name the client greeted with (incoming).</summary>
    public string ClientHostName { get; set; } = string.Empty;

    /// <summary>TLS version and cipher of the connection, or null when unencrypted.</summary>
    public string? TransportTls { get; set; }

    /// <summary>The submitting user, when authenticated.</summary>
    public string? AuthenticatedUser { get; set; }

    /// <summary>Size of the evidence file in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>SHA-256 of the file, lower-case hex.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Path under the evidence root, forward slashes.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>True for copies made later from stored mail, not captured as received.</summary>
    public bool Reconstructed { get; set; }

    /// <summary>For outgoing mail: the Sent copy it belongs to, or null (for example mail sent through the API).</summary>
    public System.Guid? SentMessageId { get; set; }

    /// <summary>pending, accepted, not-accepted or sent.</summary>
    public string Outcome { get; set; } = "pending";

    /// <summary>Days kept after the last mailbox copy is deleted.</summary>
    public int RetentionDays { get; set; } = 1095;

    /// <summary>When the last mailbox copy was deleted, or null.</summary>
    public System.DateTimeOffset? AllCopiesDeletedAt { get; set; }

    /// <summary>When it may be purged, or null while copies exist.</summary>
    public System.DateTimeOffset? PurgeAfter { get; set; }

    /// <summary>When it was purged, or null.</summary>
    public System.DateTimeOffset? PurgedAt { get; set; }

    /// <summary>Why it was purged.</summary>
    public string? PurgeReason { get; set; }
}

/// <summary>One outgoing delivery attempt and the receiving server's reply.</summary>
public sealed class EvidenceAttemptRow
{
    /// <summary>The evidence copy of the message.</summary>
    public System.Guid EvidenceId { get; set; }

    /// <summary>When the attempt ended.</summary>
    public System.DateTimeOffset AttemptedAt { get; set; }

    /// <summary>The recipient.</summary>
    public string Recipient { get; set; } = string.Empty;

    /// <summary>The receiving host.</summary>
    public string RemoteHost { get; set; } = string.Empty;

    /// <summary>TLS version and cipher, or null.</summary>
    public string? TransportTls { get; set; }

    /// <summary>The reply code, or null when there was none (connection failure).</summary>
    public int? ReplyCode { get; set; }

    /// <summary>The reply text, or the reason there was none.</summary>
    public string ReplyText { get; set; } = string.Empty;

    /// <summary>delivered, deferred or refused.</summary>
    public string Outcome { get; set; } = "deferred";
}

/// <summary>One day's manifest in the evidence chain.</summary>
public sealed class EvidenceManifestRow
{
    /// <summary>The day it covers (UTC).</summary>
    public System.DateOnly Day { get; set; }

    /// <summary>SHA-256 of the manifest file.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>SHA-256 of the previous day's manifest (64 zeros for the first).</summary>
    public string PreviousSha256 { get; set; } = string.Empty;

    /// <summary>Copies added that day.</summary>
    public int Added { get; set; }

    /// <summary>Copies purged that day.</summary>
    public int Purged { get; set; }

    /// <summary>Path under the evidence root.</summary>
    public string Path { get; set; } = string.Empty;
}
