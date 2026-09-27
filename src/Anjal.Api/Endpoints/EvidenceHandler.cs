using Anjal.Mailbox;
using Anjal.Store;

namespace Anjal.Api.Endpoints;

/// <summary>
/// v1.0.0-rc.8 (SPEC-08 R-02, R-03, R-12, R-13): the evidence store and the
/// one-off corrections, for the Anjal operator. Every evidence read is audited
/// here (the server audits only changes). Corrections are dry runs unless
/// <c>?apply=true</c>.
/// </summary>
public sealed class EvidenceHandler
{
    private readonly IMessageStore audit;
    private readonly IEvidenceStore store;
    private readonly IMailboxStore mailboxes;
    private readonly EvidenceVault vault;
    private readonly EvidenceWorker worker;
    private readonly EvidenceMaintenance maintenance;

    /// <summary>Construct.</summary>
    /// <param name="audit">Where reads are audited.</param>
    /// <param name="store">The evidence records.</param>
    /// <param name="mailboxes">Tenants, mailboxes, messages and trusted senders.</param>
    /// <param name="maildir">The stored message files.</param>
    /// <param name="vault">The evidence files.</param>
    /// <param name="hostName">This server's name.</param>
    public EvidenceHandler(IMessageStore audit, IEvidenceStore store, IMailboxStore mailboxes, IMaildirStore maildir, EvidenceVault vault, string hostName)
    {
        System.ArgumentNullException.ThrowIfNull(audit);
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(mailboxes);
        System.ArgumentNullException.ThrowIfNull(maildir);
        System.ArgumentNullException.ThrowIfNull(vault);
        this.audit = audit;
        this.store = store;
        this.mailboxes = mailboxes;
        this.vault = vault;
        this.worker = new EvidenceWorker(store, vault);
        this.maintenance = new EvidenceMaintenance(mailboxes, store, maildir, vault, hostName);
    }

    /// <summary>GET /api/evidence/{id}: details and delivery attempts.</summary>
    /// <param name="ctx">The request.</param>
    /// <param name="id">The evidence id.</param>
    /// <returns>A task.</returns>
    public async System.Threading.Tasks.Task GetAsync(RequestContext ctx, System.Guid id)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        EvidenceRow? row = await this.store.GetEvidenceAsync(id).ConfigureAwait(false);
        await this.AuditReadAsync(ctx, "details", id, row is null ? "not found" : "found").ConfigureAwait(false);
        if (row is null)
        {
            await ctx.WriteErrorAsync(404, "not_found", "No evidence with that id.").ConfigureAwait(false);
            return;
        }
        IReadOnlyList<EvidenceAttemptRow> attempts = await this.store.ListEvidenceAttemptsAsync(id).ConfigureAwait(false);
        await ctx.WriteJsonAsync(200, new
        {
            id = row.Id,
            direction = row.Direction,
            capturedAt = row.CapturedAt,
            envelopeFrom = row.EnvelopeFrom,
            envelopeTo = row.EnvelopeTo,
            remoteAddress = row.RemoteAddress,
            clientHostName = row.ClientHostName,
            transportTls = row.TransportTls,
            authenticatedUser = row.AuthenticatedUser,
            sizeBytes = row.SizeBytes,
            sha256 = row.Sha256,
            reconstructed = row.Reconstructed,
            outcome = row.Outcome,
            retentionDays = row.RetentionDays,
            allCopiesDeletedAt = row.AllCopiesDeletedAt,
            purgeAfter = row.PurgeAfter,
            purgedAt = row.PurgedAt,
            purgeReason = row.PurgeReason,
            attempts = attempts.Select(a => new { a.AttemptedAt, a.Recipient, a.RemoteHost, a.TransportTls, a.ReplyCode, a.ReplyText, a.Outcome }),
        }).ConfigureAwait(false);
    }

    /// <summary>GET /api/evidence/{id}/raw: the exact bytes, checked against the recorded SHA-256 first.</summary>
    /// <param name="ctx">The request.</param>
    /// <param name="id">The evidence id.</param>
    /// <returns>A task.</returns>
    public async System.Threading.Tasks.Task GetRawAsync(RequestContext ctx, System.Guid id)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        EvidenceRow? row = await this.store.GetEvidenceAsync(id).ConfigureAwait(false);
        if (row is null || row.PurgedAt is not null || !this.vault.Exists(row.Path))
        {
            await this.AuditReadAsync(ctx, "raw", id, row is null ? "not found" : row.PurgedAt is not null ? "purged" : "file missing").ConfigureAwait(false);
            await ctx.WriteErrorAsync(404, "not_found", row?.PurgedAt is not null ? "That evidence was purged under the retention rule." : "No evidence file with that id.").ConfigureAwait(false);
            return;
        }
        byte[] bytes = await this.vault.ReadAsync(row.Path).ConfigureAwait(false);
        string actual = EvidenceVault.Sha256Hex(bytes);
        bool intact = string.Equals(actual, row.Sha256, System.StringComparison.Ordinal);
        await this.AuditReadAsync(ctx, "raw", id, intact ? "sha256 verified" : "SHA-256 MISMATCH - not returned").ConfigureAwait(false);
        if (!intact)
        {
            await ctx.WriteErrorAsync(409, "evidence_altered", $"The file no longer matches its recorded SHA-256 ({row.Sha256}); it was not returned.").ConfigureAwait(false);
            return;
        }
        await ctx.WriteBytesAsync(200, "message/rfc822", bytes, new Dictionary<string, string>
        {
            ["X-Anjal-Evidence-Sha256"] = row.Sha256,
            ["X-Anjal-Evidence-Reconstructed"] = row.Reconstructed ? "true" : "false",
            ["Content-Disposition"] = $"attachment; filename=\"evidence-{row.Id:N}.eml\"",
        }).ConfigureAwait(false);
    }

    /// <summary>GET /api/evidence/{id}/verify: one copy against its recorded SHA-256.</summary>
    /// <param name="ctx">The request.</param>
    /// <param name="id">The evidence id.</param>
    /// <returns>A task.</returns>
    public async System.Threading.Tasks.Task VerifyOneAsync(RequestContext ctx, System.Guid id)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        EvidenceRow? row = await this.store.GetEvidenceAsync(id).ConfigureAwait(false);
        if (row is null)
        {
            await this.AuditReadAsync(ctx, "verify", id, "not found").ConfigureAwait(false);
            await ctx.WriteErrorAsync(404, "not_found", "No evidence with that id.").ConfigureAwait(false);
            return;
        }
        string? actual = row.PurgedAt is null && this.vault.Exists(row.Path) ? EvidenceVault.Sha256Hex(await this.vault.ReadAsync(row.Path).ConfigureAwait(false)) : null;
        bool intact = actual is not null && string.Equals(actual, row.Sha256, System.StringComparison.Ordinal);
        await this.AuditReadAsync(ctx, "verify", id, intact ? "intact" : "NOT intact").ConfigureAwait(false);
        await ctx.WriteJsonAsync(200, new { id = row.Id, intact, recorded = row.Sha256, actual, purged = row.PurgedAt is not null }).ConfigureAwait(false);
    }

    /// <summary>POST /api/evidence/verify: the whole manifest chain and every surviving copy.</summary>
    /// <param name="ctx">The request.</param>
    /// <returns>A task.</returns>
    public async System.Threading.Tasks.Task VerifyChainAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        IReadOnlyList<string> problems = await this.worker.VerifyAsync().ConfigureAwait(false);
        IReadOnlyList<EvidenceManifestRow> manifests = await this.store.ListEvidenceManifestsAsync().ConfigureAwait(false);
        await ctx.WriteJsonAsync(200, new { intact = problems.Count == 0, manifests = manifests.Count, problems }).ConfigureAwait(false);
    }

    /// <summary>POST /api/maintenance/transport-labels[?apply=true]: recover how past mail arrived.</summary>
    /// <param name="ctx">The request.</param>
    /// <returns>A task.</returns>
    public async System.Threading.Tasks.Task RecoverLabelsAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        LabelRecoveryReport report = await this.maintenance.RecoverTransportLabelsAsync(Apply(ctx)).ConfigureAwait(false);
        await ctx.WriteJsonAsync(200, new
        {
            applied = report.Applied,
            report.Checked,
            report.AlreadyCorrect,
            report.NoReceivedLine,
            report.Unreadable,
            changes = report.Changes.Count,
            details = report.Changes,
        }).ConfigureAwait(false);
    }

    /// <summary>POST /api/maintenance/reconstruct-evidence[?apply=true]: evidence for mail stored before rc.8.</summary>
    /// <param name="ctx">The request.</param>
    /// <returns>A task.</returns>
    public async System.Threading.Tasks.Task ReconstructAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        ReconstructionReport report = await this.maintenance.ReconstructEvidenceAsync(Apply(ctx)).ConfigureAwait(false);
        await ctx.WriteJsonAsync(200, new { applied = report.Applied, report.AlreadyKept, report.Candidates, report.Created, report.Unreadable }).ConfigureAwait(false);
    }

    /// <summary>GET /api/maintenance/trusted-senders: every mailbox's trusted senders.</summary>
    /// <param name="ctx">The request.</param>
    /// <returns>A task.</returns>
    public async System.Threading.Tasks.Task ListTrustedSendersAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        var list = new List<object>();
        foreach (TenantRow t in await this.mailboxes.ListTenantsAsync().ConfigureAwait(false))
        {
            foreach (MailboxRow m in await this.mailboxes.ListMailboxesAsync(t.Id).ConfigureAwait(false))
            {
                foreach (string sender in await this.mailboxes.ListTrustedSendersAsync(m.Id).ConfigureAwait(false))
                {
                    list.Add(new { mailbox = m.Address, sender });
                }
            }
        }
        await ctx.WriteJsonAsync(200, new { trustedSenders = list }).ConfigureAwait(false);
    }

    /// <summary>DELETE /api/maintenance/trusted-senders?mailbox=..&amp;sender=..: remove one, as the owner chooses.</summary>
    /// <param name="ctx">The request.</param>
    /// <returns>A task.</returns>
    public async System.Threading.Tasks.Task RemoveTrustedSenderAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        string? address = ctx.Query("mailbox");
        string? sender = ctx.Query("sender");
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(sender))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "Give ?mailbox=<address>&sender=<address>.").ConfigureAwait(false);
            return;
        }
        MailboxRow? mailbox = null;
        foreach (TenantRow t in await this.mailboxes.ListTenantsAsync().ConfigureAwait(false))
        {
            mailbox ??= (await this.mailboxes.ListMailboxesAsync(t.Id).ConfigureAwait(false)).FirstOrDefault(m => m.Address.Equals(address.Trim(), System.StringComparison.OrdinalIgnoreCase));
        }
        bool removed = mailbox is not null && await this.mailboxes.RemoveTrustedSenderAsync(mailbox.Id, sender.Trim()).ConfigureAwait(false);
        await ctx.WriteJsonAsync(removed ? 200 : 404, new { mailbox = address, sender, removed }).ConfigureAwait(false);
    }

    private static bool Apply(RequestContext ctx) => string.Equals(ctx.Query("apply"), "true", System.StringComparison.OrdinalIgnoreCase);

    private async System.Threading.Tasks.Task AuditReadAsync(RequestContext ctx, string what, System.Guid id, string result)
    {
        try
        {
            await this.audit.AppendAuditAsync(new AuditEvent
            {
                Actor = "api",
                Action = "READ evidence " + what,
                Subject = id.ToString(),
                Detail = result,
                RemoteAddress = ctx.RemoteAddress,
            }).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // An audit failure must not hide the answer; it is logged by the store.
        catch (System.Exception)
        {
        }
#pragma warning restore CA1031
    }
}
