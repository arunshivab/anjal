using Anjal.Store;
using Anjal.Viewing;

namespace Anjal.Webmail.Services;

/// <summary>
/// rc.29 (DES-22): attachments viewed in the mail window. The webmail opens the attachment with the person's keys
/// for this request and hands its bytes to the separate viewer process (<see cref="ViewerAccess"/>); it never reads
/// a PDF, Word, Excel or archive file itself.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>rc.29 (DES-22 §4): the viewer, when one is set up; null leaves attachments Download only.</summary>
    public ViewerAccess? Viewer { get; init; }

    /// <summary>
    /// rc.30 (D-366): a DICOM file's patient name and ID were shown to the mailbox's own person (Show patient details).
    /// The log says which mail and file, never the name or the ID.
    /// </summary>
    /// <param name="actor">Who.</param>
    /// <param name="messageId">The mail.</param>
    /// <param name="index">The attachment.</param>
    /// <param name="entry">The file inside the archive, or null.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task LogDicomPatientAsync(string actor, Guid messageId, int index, string? entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await this.messageStore.AppendAuditAsync(new AuditEvent
        {
            Actor = actor,
            Action = "webmail.dicom.patient-details",
            Subject = messageId.ToString("D") + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(entry) ? string.Empty : "/" + entry),
            Detail = "patient name and ID shown",
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// D-405 (decision A, 17 Nov 2026): a report a receiver attached (DMARC or TLS), read in the viewer process like
    /// every other outside file - the webmail never unpacks or parses it. The answer says whether the viewer looked
    /// at it: a report not looked at (no viewer, or the viewer busy) is tried again later; one it looked at and could
    /// not read is not a report.
    /// </summary>
    /// <param name="kind"><see cref="ViewKinds.DmarcReport"/> or <see cref="ViewKinds.TlsReport"/>.</param>
    /// <param name="bytes">The attachment.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether the viewer looked, and its JSON when it was a report.</returns>
    public async Task<(bool Looked, string? Json)> ReadReportAsync(string kind, byte[] bytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(bytes);
        if (kind is not (ViewKinds.DmarcReport or ViewKinds.TlsReport) || this.Viewer is not ViewerAccess { Enabled: true } viewer)
        {
            return (false, null);
        }
        ViewResult r = await viewer.AskOnceAsync(new ViewRequest { Kind = kind }, new[] { bytes }, ct).ConfigureAwait(false);
        return r.Answer.Status switch
        {
            ViewStatus.Ok => (true, r.Answer.Json),
            ViewStatus.Unavailable or ViewStatus.Busy => (false, null),
            _ => (true, null),
        };
    }

    /// <summary>The key a file's password is kept under: the mail, the attachment and the entry inside it.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The mail.</param>
    /// <param name="index">The attachment.</param>
    /// <param name="entry">The entry inside an archive, or null.</param>
    /// <returns>The key.</returns>
    public static string FileKey(Guid mailboxId, Guid messageId, int index, string? entry) =>
        mailboxId.ToString("N") + "/" + messageId.ToString("N") + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + (entry ?? string.Empty);

    /// <summary>
    /// Asks the viewer to do one job on an attachment (or a file inside it), with the password the person typed for
    /// it in this session, if any.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The mail.</param>
    /// <param name="index">The attachment.</param>
    /// <param name="request">The job (kind, page, width, entry...).</param>
    /// <param name="session">The signed-in session, for what is kept (D-362).</param>
    /// <param name="language">The reader's language ("en", "ta"...), for number and date formats.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The answer; <see cref="ViewStatus.Unavailable"/> when there is no viewer.</returns>
    public async Task<ViewResult> ViewAsync(Guid mailboxId, Guid messageId, int index, ViewRequest request, string session, string? language, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);
        if (this.Viewer is not ViewerAccess viewer)
        {
            return ViewResult.Of(ViewStatus.Unavailable);
        }
        string fileKey = FileKey(mailboxId, messageId, index, request.Entry);
        ViewRequest full = ViewerAccess.With(request, viewer.PasswordFor(session, fileKey), viewer.WhoKey(mailboxId), CultureOf(language));
        if (!string.IsNullOrEmpty(request.Entry))
        {
            // AC-20: a file inside a locked archive also needs the archive's password, and the inner archive's for
            // "3/5"; each was typed for its own archive and is kept under it.
            string[] places = request.Entry.Split('/');
            full = ViewerAccess.WithArchive(
                full,
                viewer.PasswordFor(session, FileKey(mailboxId, messageId, index, null)),
                places.Length > 1 ? viewer.PasswordFor(session, FileKey(mailboxId, messageId, index, places[0])) : null);
        }
        return await viewer.AskAsync(session, fileKey, full, async () =>
        {
            (AttachmentView View, byte[] Bytes)? found = await this.GetAttachmentAsync(mailboxId, messageId, index, ct).ConfigureAwait(false);
            return found?.Bytes;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>The language and region the viewer formats numbers and dates in, for a reader's language.</summary>
    /// <param name="language">"en", "ta", "ml", "hi", "mr" or "gu".</param>
    /// <returns>"en-IN", "ta-IN"...</returns>
    public static string CultureOf(string? language) => (language ?? "en") switch
    {
        "ta" => "ta-IN",
        "ml" => "ml-IN",
        "hi" => "hi-IN",
        "mr" => "mr-IN",
        "gu" => "gu-IN",
        _ => "en-IN",
    };
}
