using System.Text;
using System.Text.RegularExpressions;
using Anjal.Store;

namespace Anjal.Mailbox;

/// <summary>
/// One-off corrections for mail stored before v1.0.0-rc.8 (SPEC-08 R-02, R-12).
/// Both run as a dry run first. Neither changes a message file.
/// <list type="bullet">
/// <item><b>Label recovery</b> (DEF-065): how each message arrived, read back from
/// the <c>Received:</c> line this server added to it - the exact TLS version and
/// cipher since rc.7; <c>ESMTPS</c> (encrypted) or <c>ESMTP</c> before (RFC 3848).</item>
/// <item><b>Reconstruction</b>: an evidence copy of each stored message without
/// one, with the lines this server added removed, marked <i>reconstructed</i> -
/// never presented as received.</item>
/// </list>
/// </summary>
public sealed partial class EvidenceMaintenance
{
    private readonly IMailboxStore mailboxes;
    private readonly IEvidenceStore evidence;
    private readonly IMaildirStore maildir;
    private readonly EvidenceVault vault;
    private readonly string hostName;
    private readonly System.Func<System.DateTimeOffset> clock;

    /// <summary>Construct.</summary>
    /// <param name="mailboxes">Tenants, mailboxes and messages.</param>
    /// <param name="evidence">The evidence records.</param>
    /// <param name="maildir">The stored message files.</param>
    /// <param name="vault">The evidence files.</param>
    /// <param name="hostName">This server's name, as written in its Received lines.</param>
    /// <param name="clock">Time source; null for the system clock.</param>
    public EvidenceMaintenance(IMailboxStore mailboxes, IEvidenceStore evidence, IMaildirStore maildir, EvidenceVault vault, string hostName, System.Func<System.DateTimeOffset>? clock = null)
    {
        System.ArgumentNullException.ThrowIfNull(mailboxes);
        System.ArgumentNullException.ThrowIfNull(evidence);
        System.ArgumentNullException.ThrowIfNull(maildir);
        System.ArgumentNullException.ThrowIfNull(vault);
        System.ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        this.mailboxes = mailboxes;
        this.evidence = evidence;
        this.maildir = maildir;
        this.vault = vault;
        this.hostName = hostName;
        this.clock = clock ?? (() => System.DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// How a message arrived, from the first <c>Received:</c> line naming this
    /// server. Null when there is none (mail created on this server).
    /// </summary>
    /// <param name="raw">The stored message.</param>
    /// <param name="hostName">This server's name.</param>
    /// <returns>The protocol word (ESMTP, ESMTPS, ESMTPA, ESMTPSA) and TLS text, or null.</returns>
    public static (string Protocol, string? Tls)? ReadOwnReceived(byte[] raw, string hostName)
    {
        System.ArgumentNullException.ThrowIfNull(raw);
        System.ArgumentNullException.ThrowIfNull(hostName);
        foreach ((string name, string value, _) in HeaderFields(raw))
        {
            if (!name.Equals("Received", System.StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            Match m = OwnReceived().Match(value);
            if (m.Success && m.Groups["host"].Value.Equals(hostName, System.StringComparison.OrdinalIgnoreCase))
            {
                return (m.Groups["proto"].Value.ToUpperInvariant(), m.Groups["tls"].Success ? m.Groups["tls"].Value : null);
            }
        }
        return null;
    }

    /// <summary>
    /// The message without the lines this server added at the top - its
    /// Received line, its spam verdict and its Authentication-Results line -
    /// stopping at the first line it did not add.
    /// </summary>
    /// <param name="raw">The stored message.</param>
    /// <param name="hostName">This server's name.</param>
    /// <returns>The message as it was before this server added to it (to the extent that can be known).</returns>
    public static byte[] RemoveOwnAdditions(byte[] raw, string hostName)
    {
        System.ArgumentNullException.ThrowIfNull(raw);
        System.ArgumentNullException.ThrowIfNull(hostName);
        int cut = 0;
        bool receivedSeen = false;
        foreach ((string name, string value, int end) in HeaderFields(raw))
        {
            bool ours =
                (cut == 0 && name.Equals("Return-Path", System.StringComparison.OrdinalIgnoreCase))
                || (!receivedSeen && name.Equals("Received", System.StringComparison.OrdinalIgnoreCase)
                    && OwnReceived().Match(value) is { Success: true } m && m.Groups["host"].Value.Equals(hostName, System.StringComparison.OrdinalIgnoreCase))
                || name.StartsWith("X-Anjal-Spam-", System.StringComparison.OrdinalIgnoreCase)
                || (name.Equals("Authentication-Results", System.StringComparison.OrdinalIgnoreCase)
                    && value.TrimStart().StartsWith(hostName + ";", System.StringComparison.OrdinalIgnoreCase));
            if (!ours)
            {
                break;
            }
            receivedSeen |= name.Equals("Received", System.StringComparison.OrdinalIgnoreCase);
            cut = end;
        }
        return raw[cut..];
    }

    /// <summary>Recover every stored message's record of how it arrived.</summary>
    /// <param name="apply">False for a dry run: report only.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The report.</returns>
    public async Task<LabelRecoveryReport> RecoverTransportLabelsAsync(bool apply, CancellationToken ct = default)
    {
        var report = new LabelRecoveryReport { Applied = apply };
        await foreach ((TenantRow tenant, MailboxRow mailbox, FolderRow folder, MessageRow message) in this.AllMessagesAsync(ct).ConfigureAwait(false))
        {
            report.Checked++;
            byte[]? raw = await this.maildir.ReadAsync(tenant.Slug, mailbox.Address, folder.Name, message.MaildirFile, ct).ConfigureAwait(false);
            if (raw is null)
            {
                report.Unreadable++;
                continue;
            }
            (string Protocol, string? Tls)? own = ReadOwnReceived(raw, this.hostName);
            if (own is null)
            {
                report.NoReceivedLine++;
                continue;
            }
            bool? encrypted;
            string? tls;
            if (own.Value.Protocol.EndsWith('A'))
            {
                // Submitted by a signed-in user: not a question of how another server sent it.
                encrypted = null;
                tls = null;
            }
            else
            {
                // RFC 3848: ESMTPS / ESMTPSA mean TLS was in use. Not "contains S" -
                // "ESMTP" itself contains an S.
                encrypted = own.Value.Protocol.StartsWith("ESMTPS", System.StringComparison.Ordinal);
                tls = encrypted == true ? own.Value.Tls : null;
            }
            if (message.TransportEncrypted == encrypted && string.Equals(message.TransportTls, tls, System.StringComparison.Ordinal))
            {
                report.AlreadyCorrect++;
                continue;
            }
            report.Changes.Add(new LabelChange(message.Id, mailbox.Address, folder.Name, message.Subject, message.TransportEncrypted, message.TransportTls, encrypted, tls));
            if (apply)
            {
                await this.evidence.UpdateMessageTransportAsync(message.Id, encrypted, tls, ct).ConfigureAwait(false);
            }
        }
        return report;
    }

    /// <summary>Make a reconstructed evidence copy of every stored message that has none.</summary>
    /// <param name="apply">False for a dry run: report only.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The report.</returns>
    public async Task<ReconstructionReport> ReconstructEvidenceAsync(bool apply, CancellationToken ct = default)
    {
        var report = new ReconstructionReport { Applied = apply };
        await foreach ((TenantRow tenant, MailboxRow mailbox, FolderRow folder, MessageRow message) in this.AllMessagesAsync(ct).ConfigureAwait(false))
        {
            if (message.EvidenceId is not null)
            {
                report.AlreadyKept++;
                continue;
            }
            report.Candidates++;
            byte[]? raw = await this.maildir.ReadAsync(tenant.Slug, mailbox.Address, folder.Name, message.MaildirFile, ct).ConfigureAwait(false);
            if (raw is null)
            {
                report.Unreadable++;
                continue;
            }
            if (!apply)
            {
                continue;
            }
            byte[] original = RemoveOwnAdditions(raw, this.hostName);
            System.DateTimeOffset now = this.clock();
            var id = System.Guid.NewGuid();
            (string path, long size, string sha) = await this.vault.WriteAsync(id, now, original, ct).ConfigureAwait(false);
            bool sent = folder.Name.Equals("Sent", System.StringComparison.OrdinalIgnoreCase);
            await this.evidence.InsertEvidenceAsync(new EvidenceRow
            {
                Id = id,
                Direction = sent ? EvidenceRow.Out : EvidenceRow.In,
                CapturedAt = now,
                EnvelopeFrom = message.EnvelopeFrom,
                EnvelopeTo = new[] { mailbox.Address },
                TransportTls = message.TransportTls,
                SizeBytes = size,
                Sha256 = sha,
                Path = path,
                Reconstructed = true,
                Outcome = sent ? "sent" : "accepted",
                RetentionDays = tenant.EvidenceRetentionDays,
            }, ct).ConfigureAwait(false);
            await this.evidence.SetMessageEvidenceAsync(message.Id, id, ct).ConfigureAwait(false);
            report.Created++;
        }
        return report;
    }

    private async IAsyncEnumerable<(TenantRow Tenant, MailboxRow Mailbox, FolderRow Folder, MessageRow Message)> AllMessagesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (TenantRow tenant in await this.mailboxes.ListTenantsAsync(ct).ConfigureAwait(false))
        {
            foreach (MailboxRow mailbox in await this.mailboxes.ListMailboxesAsync(tenant.Id, ct).ConfigureAwait(false))
            {
                Dictionary<System.Guid, FolderRow> folders = (await this.mailboxes.ListFoldersAsync(mailbox.Id, ct).ConfigureAwait(false)).ToDictionary(f => f.Id);
                const int Page = 500;
                for (int offset = 0; ; offset += Page)
                {
                    IReadOnlyList<MessageRow> page = await this.mailboxes.ListMessagesAsync(mailbox.Id, null, Page, offset, ct).ConfigureAwait(false);
                    foreach (MessageRow m in page)
                    {
                        if (folders.TryGetValue(m.FolderId, out FolderRow? folder))
                        {
                            yield return (tenant, mailbox, folder, m);
                        }
                    }
                    if (page.Count < Page)
                    {
                        break;
                    }
                }
            }
        }
    }

    /// <summary>The header fields at the top of a message: name, unfolded value, and the byte offset just past the field.</summary>
    private static IEnumerable<(string Name, string Value, int End)> HeaderFields(byte[] raw)
    {
        int pos = 0;
        while (pos < raw.Length)
        {
            if (raw[pos] == (byte)'\r' || raw[pos] == (byte)'\n')
            {
                yield break; // the blank line ending the header
            }
            int end = pos;
            while (true)
            {
                int nl = System.Array.IndexOf(raw, (byte)'\n', end);
                end = nl < 0 ? raw.Length : nl + 1;
                if (end >= raw.Length || (raw[end] != (byte)' ' && raw[end] != (byte)'\t'))
                {
                    break;
                }
            }
            string field = Encoding.Latin1.GetString(raw, pos, end - pos);
            int colon = field.IndexOf(':', System.StringComparison.Ordinal);
            if (colon <= 0)
            {
                yield break;
            }
            string value = field[(colon + 1)..].Replace("\r\n", " ", System.StringComparison.Ordinal).Replace('\n', ' ').Replace('\t', ' ');
            yield return (field[..colon].Trim(), value, end);
            pos = end;
        }
    }

    [GeneratedRegex(@"\bby\s+(?<host>\S+)\s+with\s+(?<proto>E?SMTP[SA]*)\s+id\s+\S+(?:\s+\((?<tls>[^)]+)\))?\s*;", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OwnReceived();
}

/// <summary>One message whose record of how it arrived would change.</summary>
/// <param name="MessageId">The message.</param>
/// <param name="Mailbox">Its mailbox.</param>
/// <param name="Folder">Its folder.</param>
/// <param name="Subject">Its subject.</param>
/// <param name="WasEncrypted">The recorded value.</param>
/// <param name="WasTls">The recorded TLS.</param>
/// <param name="Encrypted">The recovered value.</param>
/// <param name="Tls">The recovered TLS.</param>
public sealed record LabelChange(System.Guid MessageId, string Mailbox, string Folder, string Subject, bool? WasEncrypted, string? WasTls, bool? Encrypted, string? Tls);

/// <summary>The result of label recovery.</summary>
public sealed class LabelRecoveryReport
{
    /// <summary>Whether changes were applied (false: dry run).</summary>
    public bool Applied { get; init; }

    /// <summary>Messages examined.</summary>
    public int Checked { get; set; }

    /// <summary>Messages whose record was already right.</summary>
    public int AlreadyCorrect { get; set; }

    /// <summary>Messages with no Received line from this server (created here).</summary>
    public int NoReceivedLine { get; set; }

    /// <summary>Messages whose file could not be read.</summary>
    public int Unreadable { get; set; }

    /// <summary>The changes (made, or that would be made).</summary>
    public List<LabelChange> Changes { get; } = new();
}

/// <summary>The result of reconstruction.</summary>
public sealed class ReconstructionReport
{
    /// <summary>Whether copies were made (false: dry run).</summary>
    public bool Applied { get; init; }

    /// <summary>Messages that already have evidence.</summary>
    public int AlreadyKept { get; set; }

    /// <summary>Messages without evidence.</summary>
    public int Candidates { get; set; }

    /// <summary>Reconstructed copies made.</summary>
    public int Created { get; set; }

    /// <summary>Messages whose file could not be read.</summary>
    public int Unreadable { get; set; }
}
