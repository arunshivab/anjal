namespace Anjal.Store;

/// <summary>
/// What the dashboard and Today need to know about one message, without its body
/// (rc.16, design review sections 7 and 8): its folder, who it was from and to, when it
/// arrived, and the marks the figures count.
/// </summary>
/// <param name="Id">The message.</param>
/// <param name="Folder">Its folder's name.</param>
/// <param name="FromHeader">The From header as stored.</param>
/// <param name="ToHeader">The To header as stored.</param>
/// <param name="EnvelopeFrom">The envelope sender.</param>
/// <param name="Subject">The subject.</param>
/// <param name="ReceivedAt">When it arrived (or was sent).</param>
/// <param name="Seen">Read.</param>
/// <param name="Answered">Answered.</param>
/// <param name="HasAttachments">Carries attachments.</param>
/// <param name="SizeBytes">Its size.</param>
/// <param name="SpamScore">The spam score given on arrival.</param>
/// <param name="SpamChecked">Whether it went through the incoming checks.</param>
/// <param name="TransportEncrypted">Whether it arrived encrypted (null for own mail).</param>
/// <param name="CategoryId">Its category, if any.</param>
public sealed record MessageFact(
    System.Guid Id,
    string Folder,
    string FromHeader,
    string ToHeader,
    string EnvelopeFrom,
    string Subject,
    System.DateTimeOffset ReceivedAt,
    bool Seen,
    bool Answered,
    bool HasAttachments,
    long SizeBytes,
    int SpamScore,
    bool? SpamChecked,
    bool? TransportEncrypted,
    System.Guid? CategoryId);
