namespace Anjal.Store;

/// <summary>
/// v1.0.0-rc.8 evidence store in memory. Deleting the last mailbox copy of a
/// message starts its evidence's retention clock, as the PostgreSQL trigger does.
/// </summary>
public sealed partial class InMemoryMessageStore
{
    private readonly Dictionary<System.Guid, EvidenceRow> evidence = new();
    private readonly List<EvidenceAttemptRow> evidenceAttempts = new();
    private readonly List<EvidenceManifestRow> evidenceManifests = new();
    private readonly Dictionary<System.Guid, (System.Guid? EvidenceId, System.Guid? SentMessageId)> outboundEvidence = new();

    /// <summary>The clock used to start retention clocks; tests may replace it.</summary>
    public System.Func<System.DateTimeOffset> EvidenceClock { get; set; } = () => System.DateTimeOffset.UtcNow;

    /// <inheritdoc/>
    public Task InsertEvidenceAsync(EvidenceRow row, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(row);
        lock (this.gate)
        {
            if (!this.evidence.TryAdd(row.Id, Copy(row)))
            {
                throw new System.InvalidOperationException("evidence " + row.Id + " already exists");
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SetEvidenceOutcomeAsync(System.Guid id, string outcome, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(outcome);
        lock (this.gate)
        {
            if (this.evidence.TryGetValue(id, out EvidenceRow? e))
            {
                e.Outcome = outcome;
                if (outcome == "not-accepted")
                {
                    e.PurgeAfter = this.EvidenceClock();
                }
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RaiseEvidenceRetentionAsync(System.Guid id, int retentionDays, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            if (this.evidence.TryGetValue(id, out EvidenceRow? e))
            {
                e.RetentionDays = System.Math.Max(e.RetentionDays, retentionDays);
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<EvidenceRow?> GetEvidenceAsync(System.Guid id, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            return Task.FromResult(this.evidence.TryGetValue(id, out EvidenceRow? e) ? Copy(e) : null);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<EvidenceRow>> ListEvidenceCapturedAsync(System.DateTimeOffset startInclusive, System.DateTimeOffset endExclusive, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<EvidenceRow> list = this.evidence.Values.Where(e => e.CapturedAt >= startInclusive && e.CapturedAt < endExclusive).OrderBy(e => e.CapturedAt).ThenBy(e => e.Id).Select(Copy).ToList();
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<EvidenceRow>> ListEvidencePurgedAsync(System.DateTimeOffset startInclusive, System.DateTimeOffset endExclusive, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<EvidenceRow> list = this.evidence.Values.Where(e => e.PurgedAt >= startInclusive && e.PurgedAt < endExclusive).OrderBy(e => e.PurgedAt).ThenBy(e => e.Id).Select(Copy).ToList();
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<EvidenceRow>> ListEvidenceDueAsync(System.DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<EvidenceRow> list = this.evidence.Values.Where(e => e.PurgedAt is null && e.PurgeAfter <= now).OrderBy(e => e.PurgeAfter).ThenBy(e => e.Id).Take(limit).Select(Copy).ToList();
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc/>
    public Task MarkEvidencePurgedAsync(System.Guid id, System.DateTimeOffset at, string reason, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(reason);
        lock (this.gate)
        {
            if (this.evidence.TryGetValue(id, out EvidenceRow? e) && e.PurgedAt is null)
            {
                e.PurgedAt = at;
                e.PurgeReason = reason;
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task AddEvidenceAttemptAsync(EvidenceAttemptRow row, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(row);
        lock (this.gate)
        {
            this.evidenceAttempts.Add(row);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<EvidenceAttemptRow>> ListEvidenceAttemptsAsync(System.Guid id, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<EvidenceAttemptRow> list = this.evidenceAttempts.Where(a => a.EvidenceId == id).OrderBy(a => a.AttemptedAt).ToList();
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc/>
    public Task<(System.Guid? EvidenceId, System.Guid? SentMessageId)> GetOutboundEvidenceLinkAsync(System.Guid outboundId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            return Task.FromResult(this.outboundEvidence.TryGetValue(outboundId, out var link) ? link : ((System.Guid?)null, (System.Guid?)null));
        }
    }

    /// <inheritdoc/>
    public Task SetOutboundEvidenceAsync(System.Guid outboundId, System.Guid evidenceId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            this.outboundEvidence.TryGetValue(outboundId, out var link);
            this.outboundEvidence[outboundId] = (evidenceId, link.SentMessageId);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task LinkOutboundToSentCopyAsync(IReadOnlyList<System.Guid> outboundIds, System.Guid sentMessageId, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(outboundIds);
        lock (this.gate)
        {
            foreach (System.Guid id in outboundIds)
            {
                this.outboundEvidence.TryGetValue(id, out var link);
                this.outboundEvidence[id] = (link.EvidenceId, sentMessageId);
                if (link.EvidenceId is System.Guid e && this.evidence.TryGetValue(e, out EvidenceRow? row))
                {
                    row.SentMessageId = sentMessageId;
                }
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<int> StartClocksForOutboundWithoutMailboxCopyAsync(System.DateTimeOffset capturedBefore, CancellationToken ct = default)
    {
        int n = 0;
        lock (this.gate)
        {
            foreach (EvidenceRow e in this.evidence.Values.Where(e => e.Direction == EvidenceRow.Out && e.SentMessageId is null && e.AllCopiesDeletedAt is null && e.CapturedAt < capturedBefore))
            {
                e.AllCopiesDeletedAt = e.CapturedAt;
                e.PurgeAfter = e.CapturedAt.AddDays(e.RetentionDays);
                n++;
            }
        }
        return Task.FromResult(n);
    }

    /// <inheritdoc/>
    public Task UpdateMessageTransportAsync(System.Guid messageId, bool? encrypted, string? tls, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            foreach (MessageRow m in this.mailboxMessages.Where(m => m.Id == messageId))
            {
                m.TransportEncrypted = encrypted;
                m.TransportTls = tls;
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> SetMessageEvidenceAsync(System.Guid messageId, System.Guid evidenceId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            MessageRow? m = this.mailboxMessages.FirstOrDefault(m => m.Id == messageId && m.EvidenceId is null);
            if (m is null)
            {
                return Task.FromResult(false);
            }
            m.EvidenceId = evidenceId;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc/>
    public Task<EvidenceManifestRow?> GetLatestEvidenceManifestAsync(CancellationToken ct = default)
    {
        lock (this.gate)
        {
            return Task.FromResult(this.evidenceManifests.OrderByDescending(m => m.Day).FirstOrDefault());
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<EvidenceManifestRow>> ListEvidenceManifestsAsync(CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<EvidenceManifestRow> list = this.evidenceManifests.OrderBy(m => m.Day).ToList();
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc/>
    public Task InsertEvidenceManifestAsync(EvidenceManifestRow row, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(row);
        lock (this.gate)
        {
            if (this.evidenceManifests.Any(m => m.Day == row.Day))
            {
                throw new System.InvalidOperationException("manifest for " + row.Day + " already exists");
            }
            this.evidenceManifests.Add(row);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// The in-memory stand-in for the database trigger: when the last copy
    /// pointing at an evidence row is gone, its retention clock starts.
    /// Called with the gate held.
    /// </summary>
    private void StartEvidenceClocks(IEnumerable<MessageRow> removed)
    {
        System.DateTimeOffset now = this.EvidenceClock();
        foreach (System.Guid id in removed.Where(m => m.EvidenceId is not null).Select(m => m.EvidenceId!.Value).Distinct())
        {
            if (this.mailboxMessages.Any(m => m.EvidenceId == id) || !this.evidence.TryGetValue(id, out EvidenceRow? e) || e.AllCopiesDeletedAt is not null)
            {
                continue;
            }
            e.AllCopiesDeletedAt = now;
            e.PurgeAfter = now.AddDays(e.RetentionDays);
        }

        // Outgoing evidence whose Sent copy was among those removed.
        var removedIds = removed.Select(m => m.Id).ToHashSet();
        foreach (EvidenceRow e in this.evidence.Values.Where(e => e.SentMessageId is System.Guid s && removedIds.Contains(s) && e.AllCopiesDeletedAt is null))
        {
            e.AllCopiesDeletedAt = now;
            e.PurgeAfter = now.AddDays(e.RetentionDays);
        }
    }

    private static EvidenceRow Copy(EvidenceRow e) => new()
    {
        Id = e.Id,
        Direction = e.Direction,
        CapturedAt = e.CapturedAt,
        EnvelopeFrom = e.EnvelopeFrom,
        EnvelopeTo = e.EnvelopeTo.ToArray(),
        RemoteAddress = e.RemoteAddress,
        ClientHostName = e.ClientHostName,
        TransportTls = e.TransportTls,
        AuthenticatedUser = e.AuthenticatedUser,
        SizeBytes = e.SizeBytes,
        Sha256 = e.Sha256,
        Path = e.Path,
        Reconstructed = e.Reconstructed,
        Outcome = e.Outcome,
        RetentionDays = e.RetentionDays,
        AllCopiesDeletedAt = e.AllCopiesDeletedAt,
        PurgeAfter = e.PurgeAfter,
        PurgedAt = e.PurgedAt,
        PurgeReason = e.PurgeReason,
        SentMessageId = e.SentMessageId,
    };
}
