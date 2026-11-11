using System.Globalization;
using System.Text;
using Anjal.Store;

namespace Anjal.Mailbox;

/// <summary>
/// rc.28 (D-264, DES-18 §11; the owner, 26 Oct: "re encrypt later"): the one-time job that seals what was
/// kept before organisation-held encryption (rc.25) - the originals in the evidence store, and any mail
/// still waiting in the outgoing queue. An organisation's original is sealed to its recovery key, as every
/// original has been since rc.25; an individual's is left as it is (D-234). The day's manifest is never
/// rewritten: the evidence record keeps the SHA-256 the manifest lists, a dated re-seal list beside the
/// manifests records each change, and the plain file goes the way of a purged one (the backup's purge
/// list removes its copy too). A dry run only counts. A second run finds nothing to do.
/// </summary>
public sealed class EvidenceResealer
{
    private readonly IEvidenceStore evidence;
    private readonly EvidenceVault vault;
    private readonly IEvidenceKeys keys;
    private readonly IMessageStore queue;
    private readonly System.Action<string>? log;

    /// <summary>Construct.</summary>
    /// <param name="evidence">The evidence records.</param>
    /// <param name="vault">The evidence files.</param>
    /// <param name="keys">Which organisations' keys an original is sealed to.</param>
    /// <param name="queue">The outgoing queue.</param>
    /// <param name="log">Where to report; optional.</param>
    public EvidenceResealer(IEvidenceStore evidence, EvidenceVault vault, IEvidenceKeys keys, IMessageStore queue, System.Action<string>? log = null)
    {
        System.ArgumentNullException.ThrowIfNull(evidence);
        System.ArgumentNullException.ThrowIfNull(vault);
        System.ArgumentNullException.ThrowIfNull(keys);
        System.ArgumentNullException.ThrowIfNull(queue);
        this.evidence = evidence;
        this.vault = vault;
        this.keys = keys;
        this.queue = queue;
        this.log = log;
    }

    /// <summary>The clock (tests set it).</summary>
    public System.Func<System.DateTimeOffset> Clock { get; set; } = () => System.DateTimeOffset.UtcNow;

    /// <summary>Seal what was kept before rc.25.</summary>
    /// <param name="dryRun">True to count only, changing nothing.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What was found and done.</returns>
    public async Task<ResealResult> RunAsync(bool dryRun, CancellationToken ct = default)
    {
        System.DateTimeOffset now = this.Clock();
        int checkedCount = 0, sealedCount = 0, individuals = 0, already = 0, missing = 0;
        var lines = new List<string>();
        foreach (EvidenceRow row in await this.evidence.ListEvidenceCapturedAsync(System.DateTimeOffset.MinValue, now.AddMinutes(1), ct).ConfigureAwait(false))
        {
            if (row.PurgedAt is not null)
            {
                continue;
            }
            checkedCount++;
            if (row.ResealedAt is not null)
            {
                already++;
                continue;
            }
            if (!this.vault.Exists(row.Path))
            {
                missing++;
                this.log?.Invoke($"Re-seal: evidence {row.Id:N} has no file ({row.Path}); left for the evidence check to report.");
                continue;
            }
            byte[] bytes = await this.vault.ReadAsync(row.Path, ct).ConfigureAwait(false);
            if (MailEnvelope.IsEnvelope(bytes))
            {
                already++;
                continue;
            }
            if (!string.Equals(EvidenceVault.Sha256Hex(bytes), row.Sha256, System.StringComparison.Ordinal))
            {
                // Never seal what no longer matches its record: that is for the evidence check to report.
                missing++;
                this.log?.Invoke($"Re-seal: evidence {row.Id:N} does not match its recorded SHA-256; left as it is.");
                continue;
            }
            // The same organisations as an original kept today: the sender's (when on this server) and each recipient's.
            string? sender = row.Direction == EvidenceRow.In ? (string.IsNullOrEmpty(row.AuthenticatedUser) ? null : row.AuthenticatedUser) : row.EnvelopeFrom;
            IReadOnlyCollection<string> recipients = row.Direction == EvidenceRow.In ? row.EnvelopeTo : System.Array.Empty<string>();
            IReadOnlyList<byte[]> to = await this.keys.ForAsync(sender, recipients, ct).ConfigureAwait(false);
            if (to.Count == 0)
            {
                individuals++;
                continue;
            }
            sealedCount++;
            if (dryRun)
            {
                continue;
            }
            byte[] sealedBytes = MailEnvelope.SealToPublicKeys(bytes, to);
            (string path, long size, string sha) = await this.vault.WriteResealedAsync(row.Id, row.CapturedAt, sealedBytes, ct).ConfigureAwait(false);
            if (!await this.evidence.ResealEvidenceAsync(row.Id, path, size, sha, now, ct).ConfigureAwait(false))
            {
                // Another run got there first: keep its file, drop this one.
                this.vault.Delete(path);
                continue;
            }
            lines.Add(string.Join(' ', "reseal", row.Id.ToString("N"), row.Sha256, sha, path));
            this.vault.Delete(row.Path);
            await this.AppendPurgeListAsync(now, row.Path, ct).ConfigureAwait(false);
        }

        int queued = 0;
        IReadOnlyList<System.Guid> waiting = await this.queue.ListUnsealedQueuedOutboundAsync(10_000, ct).ConfigureAwait(false);
        foreach (System.Guid id in waiting)
        {
            if (await this.queue.GetOutboundByIdAsync(id, ct).ConfigureAwait(false) is not OutboundMessage message || message.RawBytes.Length == 0 || OutboundCipher.IsSealed(message.RawBytes))
            {
                continue;
            }
            queued++;
            if (!dryRun)
            {
                // The store seals as it writes: to the mail server's seal key, with the record to the sender's key.
                await this.queue.ReplaceOutboundRawAsync(id, message.RawBytes, ct).ConfigureAwait(false);
                lines.Add("queue " + id.ToString("N"));
            }
        }

        if (!dryRun && lines.Count > 0)
        {
            await this.WriteListAsync(now, lines, ct).ConfigureAwait(false);
        }
        var result = new ResealResult(dryRun, checkedCount, sealedCount, individuals, already, missing, queued);
        this.log?.Invoke(result.Describe());
        return result;
    }

    // Beside the manifests, written once per run and read-only: what was re-sealed, with both hashes.
    private async Task WriteListAsync(System.DateTimeOffset now, List<string> lines, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("anjal-evidence-reseal v1\n");
        sb.Append("at ").Append(now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append('\n');
        foreach (string line in lines)
        {
            sb.Append(line).Append('\n');
        }
        sb.Append("end ").Append(lines.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        string relative = $"manifests/reseal-{now.ToUniversalTime():yyyyMMdd-HHmmss}.txt";
        string full = this.vault.FullPath(relative);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        string temp = full + ".tmp";
        await System.IO.File.WriteAllTextAsync(temp, sb.ToString(), ct).ConfigureAwait(false);
        if (!System.OperatingSystem.IsWindows())
        {
            System.IO.File.SetUnixFileMode(temp, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.GroupRead);
        }
        System.IO.File.Move(temp, full, overwrite: false);
        this.log?.Invoke($"Re-seal: the list of changes is {relative} (SHA-256 {EvidenceVault.Sha256Hex(Encoding.UTF8.GetBytes(sb.ToString()))}).");
    }

    // The backup reads these lists to remove its own copy (EvidenceWorker does the same for a purge).
    private async Task AppendPurgeListAsync(System.DateTimeOffset now, string path, CancellationToken ct)
    {
        string list = this.vault.FullPath($"purge-lists/{now.ToUniversalTime():yyyy-MM-dd}.txt");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(list)!);
        await System.IO.File.AppendAllTextAsync(list, path + "\n", ct).ConfigureAwait(false);
    }
}

/// <summary>What a re-seal run found and did (rc.28, D-264).</summary>
/// <param name="DryRun">True when nothing was changed.</param>
/// <param name="Checked">Originals looked at (not purged).</param>
/// <param name="Sealed">Originals of organisations sealed now (or that would be, in a dry run).</param>
/// <param name="Individuals">Originals of individuals, left as they are.</param>
/// <param name="AlreadySealed">Originals sealed already (since rc.25, or by an earlier run).</param>
/// <param name="Problems">Originals without a file, or not matching their record: left for the evidence check.</param>
/// <param name="Queued">Queued messages sealed now (or that would be).</param>
public sealed record ResealResult(bool DryRun, int Checked, int Sealed, int Individuals, int AlreadySealed, int Problems, int Queued)
{
    /// <summary>One line for the operator.</summary>
    /// <returns>The line.</returns>
    public string Describe() =>
        (this.DryRun ? "Re-seal (dry run, nothing changed): " : "Re-seal: ")
        + $"{this.Checked} original(s) checked; {this.Sealed} of organisations {(this.DryRun ? "to seal" : "sealed")}, "
        + $"{this.AlreadySealed} already sealed, {this.Individuals} of individuals left as they are, {this.Problems} with a problem; "
        + $"{this.Queued} queued message(s) {(this.DryRun ? "to seal" : "sealed")}.";
}
