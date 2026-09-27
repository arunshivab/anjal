namespace Anjal.Store;

/// <summary>The database side of the evidence store (v1.0.0-rc.8, ANJAL-DES-01).</summary>
public interface IEvidenceStore
{
    /// <summary>Record an evidence copy whose file is already safely on disk.</summary>
    /// <param name="row">The copy.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task InsertEvidenceAsync(EvidenceRow row, CancellationToken ct = default);

    /// <summary>Set an incoming copy's outcome; not-accepted copies become due for purging at once.</summary>
    /// <param name="id">The copy.</param>
    /// <param name="outcome">accepted, not-accepted or sent.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task SetEvidenceOutcomeAsync(System.Guid id, string outcome, CancellationToken ct = default);

    /// <summary>Raise a copy's retention to at least <paramref name="retentionDays"/> (the longest of the tenants it reached).</summary>
    /// <param name="id">The copy.</param>
    /// <param name="retentionDays">The tenant's evidence retention.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task RaiseEvidenceRetentionAsync(System.Guid id, int retentionDays, CancellationToken ct = default);

    /// <summary>One copy, or null.</summary>
    /// <param name="id">The copy.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The copy.</returns>
    Task<EvidenceRow?> GetEvidenceAsync(System.Guid id, CancellationToken ct = default);

    /// <summary>Copies captured in [start, end), in capture order.</summary>
    /// <param name="startInclusive">Start (inclusive).</param>
    /// <param name="endExclusive">End (exclusive).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The copies.</returns>
    Task<IReadOnlyList<EvidenceRow>> ListEvidenceCapturedAsync(System.DateTimeOffset startInclusive, System.DateTimeOffset endExclusive, CancellationToken ct = default);

    /// <summary>Copies purged in [start, end), in purge order.</summary>
    /// <param name="startInclusive">Start (inclusive).</param>
    /// <param name="endExclusive">End (exclusive).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The copies.</returns>
    Task<IReadOnlyList<EvidenceRow>> ListEvidencePurgedAsync(System.DateTimeOffset startInclusive, System.DateTimeOffset endExclusive, CancellationToken ct = default);

    /// <summary>Copies due for purging at <paramref name="now"/>, oldest first.</summary>
    /// <param name="now">The time now.</param>
    /// <param name="limit">At most this many.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The copies.</returns>
    Task<IReadOnlyList<EvidenceRow>> ListEvidenceDueAsync(System.DateTimeOffset now, int limit, CancellationToken ct = default);

    /// <summary>Mark a copy purged (its file has been removed).</summary>
    /// <param name="id">The copy.</param>
    /// <param name="at">When.</param>
    /// <param name="reason">Why.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task MarkEvidencePurgedAsync(System.Guid id, System.DateTimeOffset at, string reason, CancellationToken ct = default);

    /// <summary>Record an outgoing delivery attempt.</summary>
    /// <param name="row">The attempt.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task AddEvidenceAttemptAsync(EvidenceAttemptRow row, CancellationToken ct = default);

    /// <summary>An outgoing copy's attempts, oldest first.</summary>
    /// <param name="id">The copy.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The attempts.</returns>
    Task<IReadOnlyList<EvidenceAttemptRow>> ListEvidenceAttemptsAsync(System.Guid id, CancellationToken ct = default);

    /// <summary>A queued row's evidence copy and Sent copy, when known.</summary>
    /// <param name="outboundId">The queued row.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The links.</returns>
    Task<(System.Guid? EvidenceId, System.Guid? SentMessageId)> GetOutboundEvidenceLinkAsync(System.Guid outboundId, CancellationToken ct = default);

    /// <summary>Record the evidence copy made for a queued row (signed once; every retry sends it).</summary>
    /// <param name="outboundId">The queued row.</param>
    /// <param name="evidenceId">The evidence copy.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task SetOutboundEvidenceAsync(System.Guid outboundId, System.Guid evidenceId, CancellationToken ct = default);

    /// <summary>Link queued rows (and any evidence already made for them) to the Sent copy saved for them.</summary>
    /// <param name="outboundIds">The queued rows.</param>
    /// <param name="sentMessageId">The Sent copy.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task LinkOutboundToSentCopyAsync(IReadOnlyList<System.Guid> outboundIds, System.Guid sentMessageId, CancellationToken ct = default);

    /// <summary>
    /// Start the retention clock, from its capture, of outgoing evidence that
    /// has no mailbox copy (for example mail sent through the API) and was
    /// captured before <paramref name="capturedBefore"/>.
    /// </summary>
    /// <param name="capturedBefore">Only copies captured earlier.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many clocks were started.</returns>
    Task<int> StartClocksForOutboundWithoutMailboxCopyAsync(System.DateTimeOffset capturedBefore, CancellationToken ct = default);

    /// <summary>Correct a stored message's record of how it arrived (label recovery; the message file is not touched).</summary>
    /// <param name="messageId">The message.</param>
    /// <param name="encrypted">Whether it arrived encrypted; null for mail submitted by a user or created locally.</param>
    /// <param name="tls">TLS version and cipher, or null when unknown or not encrypted.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task UpdateMessageTransportAsync(System.Guid messageId, bool? encrypted, string? tls, CancellationToken ct = default);

    /// <summary>Link a stored message to its evidence copy (only when it has none yet).</summary>
    /// <param name="messageId">The message.</param>
    /// <param name="evidenceId">The evidence copy.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when linked.</returns>
    Task<bool> SetMessageEvidenceAsync(System.Guid messageId, System.Guid evidenceId, CancellationToken ct = default);

    /// <summary>The most recent manifest, or null before the first.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The manifest.</returns>
    Task<EvidenceManifestRow?> GetLatestEvidenceManifestAsync(CancellationToken ct = default);

    /// <summary>Every manifest, oldest first.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The manifests.</returns>
    Task<IReadOnlyList<EvidenceManifestRow>> ListEvidenceManifestsAsync(CancellationToken ct = default);

    /// <summary>Record a day's manifest.</summary>
    /// <param name="row">The manifest.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task InsertEvidenceManifestAsync(EvidenceManifestRow row, CancellationToken ct = default);
}
