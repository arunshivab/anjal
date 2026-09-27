using System.Globalization;
using System.Text;
using Anjal.Store;

namespace Anjal.Mailbox;

/// <summary>
/// The evidence store's housekeeping (v1.0.0-rc.8, ANJAL-DES-01), run every
/// hour. Every step is safe to repeat:
/// <list type="bullet">
/// <item>writes a manifest for each complete UTC day not yet covered, chained
/// to the previous one, re-hashing that day's files and recording any file that
/// is missing, altered or has no database row (nothing is ever deleted to tidy up);</item>
/// <item>starts the retention clock of outgoing evidence with no mailbox copy, a day after sending;</item>
/// <item>purges copies whose retention has ended, recording each in a purge list for the backup;</item>
/// <item>warns when the disk holding the evidence is more than 80% full.</item>
/// </list>
/// </summary>
public sealed class EvidenceWorker
{
    /// <summary>The previous hash of the first manifest.</summary>
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly IEvidenceStore store;
    private readonly EvidenceVault vault;
    private readonly System.Func<System.DateTimeOffset> clock;
    private readonly System.Action<string>? log;

    /// <summary>Construct.</summary>
    /// <param name="store">The database side.</param>
    /// <param name="vault">The files.</param>
    /// <param name="clock">Time source; null for the system clock.</param>
    /// <param name="log">Log callback.</param>
    public EvidenceWorker(IEvidenceStore store, EvidenceVault vault, System.Func<System.DateTimeOffset>? clock = null, System.Action<string>? log = null)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(vault);
        this.store = store;
        this.vault = vault;
        this.clock = clock ?? (() => System.DateTimeOffset.UtcNow);
        this.log = log;
    }

    /// <summary>One pass of every housekeeping step.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What was done.</returns>
    public async Task<EvidencePassResult> RunOnceAsync(CancellationToken ct = default)
    {
        System.DateTimeOffset now = this.clock();
        int clocks = await this.store.StartClocksForOutboundWithoutMailboxCopyAsync(now.AddDays(-1), ct).ConfigureAwait(false);
        int purged = await this.PurgeDueAsync(now, ct).ConfigureAwait(false);
        int manifests = await this.WriteManifestsAsync(now, ct).ConfigureAwait(false);
        this.CheckDiskSpace();
        return new EvidencePassResult(manifests, purged, clocks);
    }

    /// <summary>
    /// Check the whole chain: each manifest file against its recorded hash and
    /// its predecessor, and every copy a manifest lists that has not been purged
    /// since against its recorded hash.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Every problem found; an empty list means the evidence is intact.</returns>
    public async Task<IReadOnlyList<string>> VerifyAsync(CancellationToken ct = default)
    {
        var problems = new List<string>();
        string previous = Genesis;
        foreach (EvidenceManifestRow m in await this.store.ListEvidenceManifestsAsync(ct).ConfigureAwait(false))
        {
            string day = m.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!string.Equals(m.PreviousSha256, previous, System.StringComparison.Ordinal))
            {
                problems.Add($"manifest {day}: its recorded previous hash does not match the manifest before it");
            }
            if (!this.vault.Exists(m.Path))
            {
                problems.Add($"manifest {day}: file missing ({m.Path})");
                previous = m.Sha256;
                continue;
            }
            byte[] bytes = await this.vault.ReadAsync(m.Path, ct).ConfigureAwait(false);
            if (!string.Equals(EvidenceVault.Sha256Hex(bytes), m.Sha256, System.StringComparison.Ordinal))
            {
                problems.Add($"manifest {day}: file altered - its SHA-256 no longer matches the one recorded");
            }
            string text = Encoding.UTF8.GetString(bytes);
            if (!text.Contains("\nprevious " + m.PreviousSha256 + "\n", System.StringComparison.Ordinal))
            {
                problems.Add($"manifest {day}: the previous hash written inside it does not match the chain");
            }
            foreach (string line in text.Split('\n'))
            {
                string[] f = line.Split(' ');
                if (f.Length >= 5 && f[0] == "add" && System.Guid.TryParse(f[3], out System.Guid id))
                {
                    EvidenceRow? row = await this.store.GetEvidenceAsync(id, ct).ConfigureAwait(false);
                    if (row is null)
                    {
                        problems.Add($"evidence {id}: listed in manifest {day} but has no database record");
                        continue;
                    }
                    if (row.PurgedAt is not null)
                    {
                        continue;
                    }
                    if (!this.vault.Exists(row.Path))
                    {
                        problems.Add($"evidence {id}: file missing ({row.Path})");
                        continue;
                    }
                    string actual = EvidenceVault.Sha256Hex(await this.vault.ReadAsync(row.Path, ct).ConfigureAwait(false));
                    if (!string.Equals(actual, f[1], System.StringComparison.Ordinal) || !string.Equals(actual, row.Sha256, System.StringComparison.Ordinal))
                    {
                        problems.Add($"evidence {id}: file altered - its SHA-256 no longer matches manifest {day}");
                    }
                }
            }
            previous = m.Sha256;
        }
        return problems;
    }

    private async Task<int> PurgeDueAsync(System.DateTimeOffset now, CancellationToken ct)
    {
        int purged = 0;
        while (true)
        {
            IReadOnlyList<EvidenceRow> due = await this.store.ListEvidenceDueAsync(now, 200, ct).ConfigureAwait(false);
            if (due.Count == 0)
            {
                return purged;
            }
            foreach (EvidenceRow e in due)
            {
                string reason = e.Outcome == "not-accepted" ? "not accepted" : "retention ended";
                this.vault.Delete(e.Path);
                await this.AppendPurgeListAsync(now, e.Path, ct).ConfigureAwait(false);
                await this.store.MarkEvidencePurgedAsync(e.Id, now, reason, ct).ConfigureAwait(false);
                purged++;
            }
        }
    }

    /// <summary>The backup reads these lists to remove its own copy of purged files (append-only otherwise).</summary>
    private async Task AppendPurgeListAsync(System.DateTimeOffset now, string path, CancellationToken ct)
    {
        string list = this.vault.FullPath($"purge-lists/{now.ToUniversalTime():yyyy-MM-dd}.txt");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(list)!);
        await System.IO.File.AppendAllTextAsync(list, path + "\n", ct).ConfigureAwait(false);
    }

    private async Task<int> WriteManifestsAsync(System.DateTimeOffset now, CancellationToken ct)
    {
        System.DateOnly today = System.DateOnly.FromDateTime(now.UtcDateTime);
        EvidenceManifestRow? latest = await this.store.GetLatestEvidenceManifestAsync(ct).ConfigureAwait(false);
        System.DateOnly day;
        if (latest is not null)
        {
            day = latest.Day.AddDays(1);
        }
        else
        {
            System.DateOnly? first = this.FirstEvidenceDay();
            if (first is null)
            {
                return 0;
            }
            day = first.Value;
        }
        string previous = latest?.Sha256 ?? Genesis;
        int written = 0;
        for (; day < today; day = day.AddDays(1))
        {
            previous = await this.WriteManifestAsync(day, previous, ct).ConfigureAwait(false);
            written++;
        }
        return written;
    }

    private System.DateOnly? FirstEvidenceDay()
    {
        if (!System.IO.Directory.Exists(this.vault.Root))
        {
            return null;
        }
        System.DateOnly? first = null;
        foreach (string file in System.IO.Directory.EnumerateFiles(this.vault.Root, "*.eml", System.IO.SearchOption.AllDirectories))
        {
            string rel = System.IO.Path.GetRelativePath(this.vault.Root, file).Replace('\\', '/');
            string[] p = rel.Split('/');
            if (p.Length == 4 && System.DateOnly.TryParseExact($"{p[0]}-{p[1]}-{p[2]}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out System.DateOnly d)
                && (first is null || d < first))
            {
                first = d;
            }
        }
        return first;
    }

    private async Task<string> WriteManifestAsync(System.DateOnly day, string previous, CancellationToken ct)
    {
        var start = new System.DateTimeOffset(day.ToDateTime(System.TimeOnly.MinValue), System.TimeSpan.Zero);
        System.DateTimeOffset end = start.AddDays(1);
        IReadOnlyList<EvidenceRow> added = await this.store.ListEvidenceCapturedAsync(start, end, ct).ConfigureAwait(false);
        IReadOnlyList<EvidenceRow> purged = await this.store.ListEvidencePurgedAsync(start, end, ct).ConfigureAwait(false);
        string dayText = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.Append("anjal-evidence-manifest v1\n");
        sb.Append("day ").Append(dayText).Append('\n');
        sb.Append("previous ").Append(previous).Append('\n');
        var alerts = new List<string>();
        var known = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (EvidenceRow e in added)
        {
            known.Add(e.Path);
            sb.Append("add ").Append(e.Sha256).Append(' ').Append(e.SizeBytes.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(e.Id.ToString("N")).Append(' ').Append(e.Path).Append('\n');
            if (e.PurgedAt is not null)
            {
                continue;
            }
            if (!this.vault.Exists(e.Path))
            {
                alerts.Add($"alert missing {e.Id:N} {e.Path}");
            }
            else if (!string.Equals(EvidenceVault.Sha256Hex(await this.vault.ReadAsync(e.Path, ct).ConfigureAwait(false)), e.Sha256, System.StringComparison.Ordinal))
            {
                alerts.Add($"alert altered {e.Id:N} {e.Path}");
            }
        }
        foreach (EvidenceRow e in purged)
        {
            sb.Append("purge ").Append(e.Sha256).Append(' ').Append(e.SizeBytes.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(e.Id.ToString("N")).Append(' ').Append((e.PurgeReason ?? string.Empty).Replace(' ', '-')).Append('\n');
        }
        string folder = this.vault.FullPath($"{day:yyyy}/{day:MM}/{day:dd}/x").TrimEnd('x');
        if (System.IO.Directory.Exists(folder))
        {
            foreach (string file in System.IO.Directory.EnumerateFiles(folder, "*.eml").Order(System.StringComparer.Ordinal))
            {
                string rel = System.IO.Path.GetRelativePath(this.vault.Root, file).Replace('\\', '/');
                if (!known.Contains(rel))
                {
                    alerts.Add($"alert orphan {rel}");
                }
            }
        }
        foreach (string a in alerts)
        {
            sb.Append(a).Append('\n');
            this.log?.Invoke($"CRIT evidence {dayText}: {a}");
        }
        sb.Append("end ").Append(added.Count.ToString(CultureInfo.InvariantCulture)).Append(' ')
          .Append(purged.Count.ToString(CultureInfo.InvariantCulture)).Append(' ')
          .Append(alerts.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');

        byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
        string relative = $"manifests/{dayText}.txt";
        string full = this.vault.FullPath(relative);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        if (!System.IO.File.Exists(full))
        {
            string temp = full + ".tmp";
            await System.IO.File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            if (!System.OperatingSystem.IsWindows())
            {
                // Part of the proof, like the evidence itself: read-only.
                System.IO.File.SetUnixFileMode(temp, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.GroupRead);
            }
            System.IO.File.Move(temp, full, overwrite: false);
        }
        else
        {
            // A manifest file without its database row (a crash in between): keep the file and use it.
            bytes = await System.IO.File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        }
        string sha = EvidenceVault.Sha256Hex(bytes);
        await this.store.InsertEvidenceManifestAsync(new EvidenceManifestRow
        {
            Day = day,
            Sha256 = sha,
            PreviousSha256 = previous,
            Added = added.Count,
            Purged = purged.Count,
            Path = relative,
        }, ct).ConfigureAwait(false);
        this.log?.Invoke($"Evidence manifest {dayText}: {added.Count} added, {purged.Count} purged, {alerts.Count} alert(s); SHA-256 {sha}.");
        return sha;
    }

    private void CheckDiskSpace()
    {
        try
        {
            string root = System.IO.Directory.Exists(this.vault.Root) ? this.vault.Root : System.IO.Path.GetDirectoryName(this.vault.Root) ?? this.vault.Root;
            var drive = new System.IO.DriveInfo(root);
            if (drive.TotalSize > 0)
            {
                double used = 1.0 - ((double)drive.AvailableFreeSpace / drive.TotalSize);
                if (used > 0.80)
                {
                    this.log?.Invoke($"WARN evidence disk {used:P0} full ({drive.AvailableFreeSpace / (1024 * 1024)} MB free) - plan to move evidence to object storage (ANJAL-DES-01).");
                }
            }
        }
#pragma warning disable CA1031 // A disk-space reading that fails must not stop housekeeping.
        catch (System.Exception ex)
        {
            this.log?.Invoke($"evidence disk space could not be read: {ex.GetType().Name}");
        }
#pragma warning restore CA1031
    }
}

/// <summary>What one housekeeping pass did.</summary>
/// <param name="ManifestsWritten">Daily manifests written.</param>
/// <param name="Purged">Copies purged.</param>
/// <param name="ClocksStarted">Retention clocks started for outgoing mail with no mailbox copy.</param>
public sealed record EvidencePassResult(int ManifestsWritten, int Purged, int ClocksStarted);
