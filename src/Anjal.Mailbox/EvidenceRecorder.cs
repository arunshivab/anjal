using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Mailbox;

/// <summary>
/// Keeps incoming originals (v1.0.0-rc.8): the file first, safely on disk,
/// then its database row. If either fails, the exception reaches the SMTP
/// session, which defers the message.
/// </summary>
public sealed class EvidenceRecorder : IEvidenceRecorder
{
    private readonly IEvidenceStore store;
    private readonly EvidenceVault vault;

    /// <summary>Construct.</summary>
    /// <param name="store">The database side.</param>
    /// <param name="vault">The files.</param>
    public EvidenceRecorder(IEvidenceStore store, EvidenceVault vault)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(vault);
        this.store = store;
        this.vault = vault;
    }

    /// <inheritdoc/>
    public async Task<System.Guid> RecordInboundAsync(InboundEvidence evidence, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(evidence);
        var id = System.Guid.NewGuid();
        (string path, long size, string sha) = await this.vault.WriteAsync(id, evidence.ReceivedAt, evidence.RawBytes, ct).ConfigureAwait(false);
        await this.store.InsertEvidenceAsync(new EvidenceRow
        {
            Id = id,
            Direction = EvidenceRow.In,
            CapturedAt = evidence.ReceivedAt,
            EnvelopeFrom = evidence.EnvelopeFrom,
            EnvelopeTo = evidence.EnvelopeTo,
            RemoteAddress = evidence.RemoteAddress,
            ClientHostName = evidence.ClientHostName,
            TransportTls = evidence.TransportTls,
            AuthenticatedUser = evidence.AuthenticatedUser,
            SizeBytes = size,
            Sha256 = sha,
            Path = path,
            Outcome = "pending",
        }, ct).ConfigureAwait(false);
        return id;
    }

    /// <inheritdoc/>
    public Task CompleteInboundAsync(System.Guid id, bool accepted, CancellationToken ct = default) =>
        this.store.SetEvidenceOutcomeAsync(id, accepted ? "accepted" : "not-accepted", ct);

    /// <summary>A queued row's existing evidence copy and Sent copy, when known.</summary>
    /// <param name="outboundId">The queued row.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The links.</returns>
    public Task<(System.Guid? EvidenceId, System.Guid? SentMessageId)> GetOutboundLinkAsync(System.Guid outboundId, CancellationToken ct = default) =>
        this.store.GetOutboundEvidenceLinkAsync(outboundId, ct);

    /// <summary>The exact bytes of an evidence copy, checked against its recorded SHA-256.</summary>
    /// <param name="id">The evidence copy.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The bytes.</returns>
    public async Task<byte[]> ReadVerifiedAsync(System.Guid id, CancellationToken ct = default)
    {
        EvidenceRow row = await this.store.GetEvidenceAsync(id, ct).ConfigureAwait(false)
            ?? throw new System.IO.FileNotFoundException("no evidence record " + id);
        byte[] bytes = await this.vault.ReadAsync(row.Path, ct).ConfigureAwait(false);
        if (!string.Equals(EvidenceVault.Sha256Hex(bytes), row.Sha256, System.StringComparison.Ordinal))
        {
            throw new System.IO.InvalidDataException("evidence " + id + " does not match its recorded SHA-256");
        }
        return bytes;
    }

    /// <summary>Keep the exact outgoing bytes (after DKIM signing) of a queued row; every retry then sends them.</summary>
    /// <param name="outbound">The queued row.</param>
    /// <param name="signedBytes">The bytes as they will be transmitted.</param>
    /// <param name="sentMessageId">The Sent copy, when known.</param>
    /// <param name="capturedAt">When.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The evidence id.</returns>
    public async Task<System.Guid> RecordOutboundAsync(OutboundMessage outbound, byte[] signedBytes, System.Guid? sentMessageId, System.DateTimeOffset capturedAt, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(outbound);
        System.ArgumentNullException.ThrowIfNull(signedBytes);
        var id = System.Guid.NewGuid();
        (string path, long size, string sha) = await this.vault.WriteAsync(id, capturedAt, signedBytes, ct).ConfigureAwait(false);
        await this.store.InsertEvidenceAsync(new EvidenceRow
        {
            Id = id,
            Direction = EvidenceRow.Out,
            CapturedAt = capturedAt,
            EnvelopeFrom = outbound.EnvelopeFrom,
            EnvelopeTo = new[] { outbound.EnvelopeTo },
            SizeBytes = size,
            Sha256 = sha,
            Path = path,
            Outcome = "pending",
            SentMessageId = sentMessageId,
        }, ct).ConfigureAwait(false);
        await this.store.SetOutboundEvidenceAsync(outbound.Id, id, ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>Record one delivery attempt and the receiving server's reply; a delivered copy is marked sent.</summary>
    /// <param name="id">The evidence copy.</param>
    /// <param name="recipient">The recipient.</param>
    /// <param name="result">The attempt's result.</param>
    /// <param name="at">When it ended.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task RecordAttemptAsync(System.Guid id, string recipient, SendResult result, System.DateTimeOffset at, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(recipient);
        System.ArgumentNullException.ThrowIfNull(result);
        string outcome = result.Outcome switch
        {
            SendOutcome.Sent => "delivered",
            SendOutcome.PermanentFailure => "refused",
            _ => "deferred",
        };
        await this.store.AddEvidenceAttemptAsync(new EvidenceAttemptRow
        {
            EvidenceId = id,
            AttemptedAt = at,
            Recipient = recipient,
            RemoteHost = result.RemoteHost,
            TransportTls = result.TransportTls,
            ReplyCode = result.ReplyCode == 0 ? null : result.ReplyCode,
            ReplyText = result.Message,
            Outcome = outcome,
        }, ct).ConfigureAwait(false);
        if (result.Outcome == SendOutcome.Sent)
        {
            await this.store.SetEvidenceOutcomeAsync(id, "sent", ct).ConfigureAwait(false);
        }
    }
}
