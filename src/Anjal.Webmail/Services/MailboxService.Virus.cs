using Anjal.Mailbox;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>One message held in quarantine (rc.22, item 39), for the Anjal console.</summary>
/// <param name="Row">The message.</param>
/// <param name="Mailbox">Whose.</param>
/// <param name="Tenant">Its organisation.</param>
/// <param name="Files">The files on it, by name.</param>
public sealed record HeldMessage(MessageRow Row, MailboxRow Mailbox, TenantRow Tenant, IReadOnlyList<string> Files);

/// <summary>
/// rc.22 (item 39, mockups 22-23): virus scanning as the webmail sees it - the scanner's state for
/// the console, mail held while the scanner was down scanned again every five minutes, the operator
/// reminded every thirty minutes while anything is still held, and the operator's Scan again and Release.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The scanner, or null when scanning is off (ANJAL_CLAMAV not set).</summary>
    public IVirusScanner? Scanner { get; init; }

    /// <summary>rc.24 (item 94): what looks at every link when a mail is shown.</summary>
    public LinkChecker Links { get; init; } = new(null, null);

    /// <summary>The operator's address for held-mail reminders (ANJAL_POSTMASTER), or empty.</summary>
    public string OperatorAddress { get; init; } = string.Empty;

    private DateTimeOffset heldReminded = DateTimeOffset.MinValue;

    /// <summary>The scanner's state: its version line when it answers, else null, and the counts.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The version (null when down or off) and the counts.</returns>
    public async Task<(string? Version, VirusStats Stats)> ScannerStateAsync(CancellationToken ct = default)
    {
        VirusStats stats = await VirusStats.ReadAsync(this.store, ct).ConfigureAwait(false);
        if (this.Scanner is null)
        {
            return (null, stats);
        }
        string? version = await this.Scanner.VersionAsync(ct).ConfigureAwait(false);
        if (version is not null && stats.Version != version)
        {
            await VirusStats.UpdateAsync(this.store, s =>
            {
                s.Version = version;
                s.LastSeen = DateTimeOffset.UtcNow;
            }, ct).ConfigureAwait(false);
        }
        return (version, stats);
    }

    /// <summary>Every message held in quarantine, across the service, oldest first.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The held messages.</returns>
    public async Task<IReadOnlyList<HeldMessage>> HeldMessagesAsync(CancellationToken ct = default)
    {
        var result = new List<HeldMessage>();
        foreach (TenantRow tenant in await this.store.ListTenantsAsync(ct).ConfigureAwait(false))
        {
            foreach (MailboxRow m in await this.store.ListMailboxesAsync(tenant.Id, ct).ConfigureAwait(false))
            {
                FolderRow? held = await this.GetFolderAsync(m.Id, AttachmentPolicy.QuarantineFolder, ct).ConfigureAwait(false);
                if (held is null)
                {
                    continue;
                }
                foreach (MessageRow row in await this.store.ListMessagesAsync(m.Id, held.Id, 200, 0, ct).ConfigureAwait(false))
                {
                    byte[]? raw = await this.maildir.ReadAsync(tenant.Slug, m.Address, held.Name, row.MaildirFile, ct).ConfigureAwait(false);
                    result.Add(new HeldMessage(row, m, tenant, raw is null ? Array.Empty<string>() : AttachmentNames(raw).Select(x => x.Name).ToList()));
                }
            }
        }
        return result.OrderBy(h => h.Row.ReceivedAt).ToList();
    }

    /// <summary>
    /// Scan held mail again (every five minutes, and from the console): delivered through the normal
    /// path once the scanner answers, which removes a virus or files a disk image in Junk as it would
    /// have on arrival. Returns how many were delivered.
    /// </summary>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<int> RescanHeldAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        IReadOnlyList<HeldMessage> held = await this.HeldMessagesAsync(ct).ConfigureAwait(false);
        if (held.Count == 0)
        {
            return 0;
        }
        int delivered = 0;
        bool scannerDown = false;
        foreach (HeldMessage h in held)
        {
            if (scannerDown)
            {
                break;
            }
            string? outcome = await this.ReleaseHeldAsync(h, scanAgain: true, ct).ConfigureAwait(false);
            if (outcome is null)
            {
                delivered++;
            }
            else if (outcome == "held")
            {
                scannerDown = true;
            }
        }
        if (scannerDown && this.OperatorAddress.Length > 0 && now - this.heldReminded >= TimeSpan.FromMinutes(30))
        {
            // Decisions of 2 Oct 2026: the operator hears at once (the server does that) and every 30 minutes after.
            this.heldReminded = now;
            int still = held.Count - delivered;
            var carrier = new MailboxRow { LocalPart = "no-reply", Domain = this.hostName, DisplayName = "Anjal" };
            await this.SendSecurityMailAsync(carrier, this.OperatorAddress, "Mail is still held: the virus scanner is not answering",
                $"{still} message(s) with files are held in quarantine because ClamAV is not answering. They are scanned again every five minutes and delivered once it does.\r\n\r\n"
                + "Check clamd: systemctl status clamav-daemon. The Anjal console, Virus scanning, lists what is held and can release a message by hand.\r\n", ct).ConfigureAwait(false);
        }
        return delivered;
    }

    /// <summary>
    /// Release one held message: scanned again and delivered as on arrival, or - the operator's
    /// decision - delivered as it is without a scan. Returns null when delivered, "held" when the
    /// scanner is still down, or the problem.
    /// </summary>
    /// <param name="held">The message.</param>
    /// <param name="scanAgain">True to scan; false to deliver unscanned.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> ReleaseHeldAsync(HeldMessage held, bool scanAgain, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(held);
        FolderRow? folder = await this.GetFolderAsync(held.Mailbox.Id, AttachmentPolicy.QuarantineFolder, ct).ConfigureAwait(false);
        byte[]? raw = folder is null ? null : await this.maildir.ReadAsync(held.Tenant.Slug, held.Mailbox.Address, folder.Name, held.Row.MaildirFile, ct).ConfigureAwait(false);
        if (raw is null)
        {
            return "The held message's file was not found.";
        }
        if (scanAgain)
        {
            Anjal.Mime.MimeMessage? parsed;
            try
            {
                parsed = Anjal.Mime.MimeParser.Parse(raw);
            }
            catch (Anjal.Mime.MimeParseException)
            {
                parsed = null;
            }
            FileJudgement judged = await AttachmentPolicy.JudgeAsync(parsed, this.Scanner, ct).ConfigureAwait(false);
            if (judged.Verdict == FileVerdict.Held)
            {
                return "held";
            }
        }
        // Delivered again through the sink, with the scanner's verdict applied there (or none, when releasing by hand).
        var sink = new MailboxSink(this.store, this.maildir) { Scanner = scanAgain ? this.Scanner : null };
        Anjal.Smtp.DeliveryResult result = await sink.DeliverAsync(new Anjal.Smtp.DeliveryContext
        {
            EnvelopeFrom = held.Row.EnvelopeFrom,
            EnvelopeTo = new[] { held.Mailbox.Address },
            RawBytes = StripFirstLine(MailboxSink.WithReturnPath(raw, string.Empty)),
            TransportTls = held.Row.TransportTls,
            EvidenceId = held.Row.EvidenceId,
        }, ct).ConfigureAwait(false);
        if (result.Outcome != Anjal.Smtp.DeliveryOutcome.Accepted)
        {
            return result.ReplyText;
        }
        // The original (quarantined) copy's row and file go; the usage counted it, so it is taken off.
        this.maildir.Delete(held.Tenant.Slug, held.Mailbox.Address, folder!.Name, held.Row.MaildirFile);
        await this.store.DeleteMessageAsync(held.Row.Id, ct).ConfigureAwait(false);
        await this.store.AddMailboxUsageAsync(held.Mailbox.Id, -held.Row.SizeBytes, ct).ConfigureAwait(false);
        return null;
    }

    // The stored file begins with the Return-Path line delivery added; the sink adds it again, so it comes off here.
    private static byte[] StripFirstLine(byte[] raw)
    {
        int nl = Array.IndexOf(raw, (byte)'\n');
        return nl < 0 ? raw : raw[(nl + 1)..];
    }
}
