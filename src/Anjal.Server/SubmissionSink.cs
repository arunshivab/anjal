namespace Anjal.Server;

/// <summary>
/// Delivery for mail submitted by an authenticated user on ports 587 and
/// 465. Local recipients are delivered directly, exactly as inbound mail is;
/// external recipients are queued for outbound delivery - DKIM-signed and
/// retried by the outbound worker, the same path webmail sending uses; and a
/// copy is filed in the sender's Sent folder when the sender is a mailbox.
/// <para>
/// Before this, submitted mail went only to the inbound path, which knows
/// local mailboxes and webhook rules and nothing else: an authenticated user
/// could log in on 587 but could not reach any outside address (DEF-002).
/// </para>
/// </summary>
public sealed class SubmissionSink : Anjal.Smtp.IMessageSink
{
    private readonly Anjal.Smtp.IMessageSink localSink;
    private readonly Anjal.Smtp.ILocalDomainResolver? localDomains;
    private readonly Anjal.Store.IMessageStore outbound;
    private readonly Anjal.Mailbox.MailboxSink? mailboxes;
    private readonly System.Action<string>? log;
    private readonly System.Func<System.DateTimeOffset> clock;

    /// <summary>Construct.</summary>
    /// <param name="localSink">Delivers to local mailboxes and webhook rules (the inbound sink).</param>
    /// <param name="localDomains">Decides which recipients are local. When null, a recipient is local if it resolves to a mailbox.</param>
    /// <param name="outbound">The outbound queue.</param>
    /// <param name="mailboxes">Files the Sent copy; null disables it.</param>
    /// <param name="log">Optional log.</param>
    /// <param name="clock">Clock, for tests.</param>
    public SubmissionSink(
        Anjal.Smtp.IMessageSink localSink,
        Anjal.Smtp.ILocalDomainResolver? localDomains,
        Anjal.Store.IMessageStore outbound,
        Anjal.Mailbox.MailboxSink? mailboxes,
        System.Action<string>? log = null,
        System.Func<System.DateTimeOffset>? clock = null)
    {
        System.ArgumentNullException.ThrowIfNull(localSink);
        System.ArgumentNullException.ThrowIfNull(outbound);
        this.localSink = localSink;
        this.localDomains = localDomains;
        this.outbound = outbound;
        this.mailboxes = mailboxes;
        this.log = log;
        this.clock = clock ?? (() => System.DateTimeOffset.UtcNow);
    }

    // rc.26 (item 99): the organisation a submission counts against - the signed-in mailbox's, else the
    // organisation owning the signed-in name's domain, else the envelope sender's domain's.
    private static async System.Threading.Tasks.Task<System.Guid?> SenderTenantAsync(Anjal.Store.IMailboxStore boxes, string? user, string envelopeDomain, System.Threading.CancellationToken ct)
    {
        string name = user ?? string.Empty;
        int at = name.LastIndexOf('@');
        if (at > 0)
        {
            if (await boxes.GetMailboxAsync(name[..at], name[(at + 1)..], ct).ConfigureAwait(false) is Anjal.Store.MailboxRow box)
            {
                return box.TenantId;
            }
            if (await boxes.GetTenantDomainAsync(name[(at + 1)..], ct).ConfigureAwait(false) is Anjal.Store.TenantDomainRow byName)
            {
                return byName.TenantId;
            }
        }
        return envelopeDomain.Length > 0 && await boxes.GetTenantDomainAsync(envelopeDomain, ct).ConfigureAwait(false) is Anjal.Store.TenantDomainRow byEnvelope
            ? byEnvelope.TenantId
            : null;
    }

    /// <inheritdoc/>
    public async System.Threading.Tasks.Task<Anjal.Smtp.DeliveryResult> DeliverAsync(Anjal.Smtp.DeliveryContext ctx, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        if (ctx.AuthenticatedUser is null)
        {
            // Submission requires a login, so this should not happen; if it
            // does, nothing is sent outside.
            return await this.localSink.DeliverAsync(ctx, ct).ConfigureAwait(false);
        }

        // v1.0.0-rc.9 (DEF-073; RFC 6409 8.2, 8.3): a missing Date or Message-ID is
        // added once, and every copy - local, outgoing, Sent - carries the same
        // message. The evidence of the submission stays as the program sent it.
        int at = ctx.EnvelopeFrom.LastIndexOf('@');
        ctx = ctx.WithRawBytes(SubmissionHeaders.Complete(ctx.RawBytes, at >= 0 ? ctx.EnvelopeFrom[(at + 1)..] : string.Empty, this.clock()));

        // rc.22 (item 51): mail an application sends offers a way out - a List-Unsubscribe header
        // pointing at unsubscribe@ its domain, which puts the recipient on the organisation's
        // do-not-send list - unless the application wrote its own.
        if (at >= 0 && (ctx.AuthenticatedUser ?? string.Empty).StartsWith("app-", System.StringComparison.OrdinalIgnoreCase)
            && !HasHeader(ctx.RawBytes, "List-Unsubscribe"))
        {
            string domain = ctx.EnvelopeFrom[(at + 1)..];
            string line = "List-Unsubscribe: <mailto:" + Anjal.Mailbox.MailboxSink.UnsubscribeLocal + "@" + domain + "?subject=unsubscribe>\r\n";
            byte[] head = System.Text.Encoding.ASCII.GetBytes(line);
            var withHeader = new byte[head.Length + ctx.RawBytes.Length];
            head.CopyTo(withHeader, 0);
            ctx.RawBytes.CopyTo(withHeader, head.Length);
            ctx = ctx.WithRawBytes(withHeader);
        }

        // rc.26 (item 85, D-173): programs, scripts and disk images are refused for mail written here too,
        // before anything is delivered or queued; the mail app shows the reason.
        Anjal.Mime.MimeMessage? parsedForFiles = null;
        try
        {
            parsedForFiles = Anjal.Mime.MimeParser.Parse(ctx.RawBytes);
        }
#pragma warning disable CA1031 // A message that cannot be parsed has no files to judge; it goes on as before.
        catch (System.Exception)
        {
            parsedForFiles = null;
        }
#pragma warning restore CA1031
        if (Anjal.Mailbox.AttachmentPolicy.RefusedInside(parsedForFiles) is string refused)
        {
            Anjal.Smtp.Counters.Increment("anjal_virus_refused_total");
            return new Anjal.Smtp.DeliveryResult
            {
                Outcome = Anjal.Smtp.DeliveryOutcome.PermanentFailure,
                ReplyText = refused + "; share them another way",
            };
        }

        var local = new System.Collections.Generic.List<string>();
        var external = new System.Collections.Generic.List<string>();
        foreach (string rcpt in ctx.EnvelopeTo)
        {
            if (await this.IsLocalAsync(rcpt, ct).ConfigureAwait(false))
            {
                local.Add(rcpt);
            }
            else
            {
                external.Add(rcpt);
            }
        }

        // rc.26 (item 99, D-219): the organisation's sends per day counted on this route too; over the
        // figure the mail app is told to try tomorrow (452), before anything is delivered or queued.
        // The organisation is the signed-in user's (a person's address, or an application key's
        // app-<id>@<domain>), never the envelope sender's: an empty MAIL FROM, or a sender address on
        // another domain, must not escape the count.
        if (external.Count > 0 && this.outbound is Anjal.Store.IMailboxStore boxes
            && await SenderTenantAsync(boxes, ctx.AuthenticatedUser, at >= 0 ? ctx.EnvelopeFrom[(at + 1)..] : string.Empty, ct).ConfigureAwait(false) is System.Guid owner)
        {
            // The day's count is kept by the sender's domain, so mail to the outside must carry one of
            // the organisation's own: an empty MAIL FROM or another domain's would not be counted.
            string envelopeDomain = at >= 0 ? ctx.EnvelopeFrom[(at + 1)..] : string.Empty;
            if (envelopeDomain.Length == 0 || await boxes.GetTenantDomainAsync(envelopeDomain, ct).ConfigureAwait(false) is not Anjal.Store.TenantDomainRow own || own.TenantId != owner)
            {
                this.log?.Invoke($"Submission: {ctx.AuthenticatedUser} sent to outside addresses from '{ctx.EnvelopeFrom}'; refused");
                return new Anjal.Smtp.DeliveryResult
                {
                    Outcome = Anjal.Smtp.DeliveryOutcome.PermanentFailure,
                    ReplyText = "Mail to outside addresses must be sent from an address of your organisation",
                };
            }
        }
        if (external.Count > 0 && this.outbound is Anjal.Store.IMailboxStore counted
            && await SenderTenantAsync(counted, ctx.AuthenticatedUser, at >= 0 ? ctx.EnvelopeFrom[(at + 1)..] : string.Empty, ct).ConfigureAwait(false) is System.Guid payer
            && await Anjal.Mailbox.SendingDay.RefusedAsync(counted, this.outbound, payer, external.Count, this.clock(), ct).ConfigureAwait(false) is string over)
        {
            this.log?.Invoke($"Submission: {over}");
            return new Anjal.Smtp.DeliveryResult
            {
                Outcome = Anjal.Smtp.DeliveryOutcome.TransientFailure,
                ReplyText = over,
            };
        }

        // Local first: if it fails for a temporary reason, nothing has been
        // queued yet, so the client's retry cannot send external copies twice.
        Anjal.Smtp.DeliveryResult? localResult = null;
        if (local.Count > 0)
        {
            // DEF-077: every property travels on, including the evidence link.
            localResult = await this.localSink.DeliverAsync(ctx.WithRecipients(local.ToArray()), ct).ConfigureAwait(false);
            if (localResult.Outcome == Anjal.Smtp.DeliveryOutcome.TransientFailure)
            {
                return localResult;
            }
        }

        System.DateTimeOffset now = this.clock();
        var queued = new System.Collections.Generic.List<System.Guid>();
        foreach (string rcpt in external)
        {
            Anjal.Store.OutboundMessage row = await this.outbound.EnqueueOutboundAsync(new Anjal.Store.OutboundMessage
            {
                EnvelopeFrom = ctx.EnvelopeFrom,
                EnvelopeTo = rcpt,
                RawBytes = ctx.RawBytes,
                CreatedAt = now,
                NextAttemptAt = now,
                GiveUpAt = now + Anjal.Store.OutboundMessage.DefaultGiveUp,
            }, ct).ConfigureAwait(false);
            queued.Add(row.Id);
        }

        bool localAccepted = localResult is { Outcome: Anjal.Smtp.DeliveryOutcome.Accepted };
        if (external.Count == 0 && !localAccepted)
        {
            // Nothing went anywhere (every recipient was an unknown local
            // address): report that, and file nothing in Sent.
            return localResult ?? new Anjal.Smtp.DeliveryResult
            {
                Outcome = Anjal.Smtp.DeliveryOutcome.PermanentFailure,
                ReplyText = "No recipients",
            };
        }

        // A copy in Sent, as webmail files one - for mailbox users only. A
        // service account (SIGMA, Lipi) logs in with its own username and has
        // no Sent folder.
        if (this.mailboxes is not null &&
            string.Equals(ctx.AuthenticatedUser, ctx.EnvelopeFrom, System.StringComparison.OrdinalIgnoreCase))
        {
            // v1.0.0-rc.8: the Sent copy points to the evidence of the submission,
            // and the queued rows (whose outgoing evidence follows) to the Sent copy.
            System.Guid? sentCopy = await this.mailboxes.FileSentCopyWithEvidenceAsync(ctx.EnvelopeFrom, ctx.RawBytes, ctx.EvidenceId, ct).ConfigureAwait(false);
            if (sentCopy is System.Guid s && this.outbound is Anjal.Store.IEvidenceStore evidenceStore)
            {
                await evidenceStore.LinkOutboundToSentCopyAsync(queued, s, ct).ConfigureAwait(false);
            }
        }

        Anjal.Smtp.Counters.Add("anjal_submission_queued_total", external.Count);
        this.log?.Invoke($"Submission from {ctx.AuthenticatedUser}: {external.Count} queued for delivery, {local.Count} local.");
        return new Anjal.Smtp.DeliveryResult
        {
            Outcome = Anjal.Smtp.DeliveryOutcome.Accepted,
            ReplyText = external.Count > 0
                ? $"Queued for delivery to {external.Count} recipient(s)" + (local.Count > 0 ? $", delivered {local.Count} locally" : string.Empty)
                : localResult!.ReplyText,
        };
    }

    private async System.Threading.Tasks.Task<bool> IsLocalAsync(string recipient, System.Threading.CancellationToken ct)
    {
        int at = recipient.LastIndexOf('@');
        if (at < 0)
        {
            return false;
        }
        if (this.localDomains is not null)
        {
            return await this.localDomains.IsLocalAsync(recipient.Substring(at + 1), ct).ConfigureAwait(false);
        }
        return this.mailboxes is not null && (await this.mailboxes.ResolveAsync(recipient, ct).ConfigureAwait(false) is not null
            || await this.mailboxes.IsApplicationAddressAsync(recipient, ct).ConfigureAwait(false));
    }

    /// <summary>Whether the header block names a header (rc.22).</summary>
    /// <param name="raw">The message.</param>
    /// <param name="name">The header's name.</param>
    /// <returns>True when present.</returns>
    public static bool HasHeader(byte[] raw, string name)
    {
        System.ArgumentNullException.ThrowIfNull(raw);
        System.ArgumentNullException.ThrowIfNull(name);
        int end = raw.Length;
        for (int i = 0; i + 1 < raw.Length; i++)
        {
            if (raw[i] == (byte)'\n' && (raw[i + 1] == (byte)'\n' || (raw[i + 1] == (byte)'\r' && i + 2 < raw.Length && raw[i + 2] == (byte)'\n')))
            {
                end = i;
                break;
            }
        }
        string head = System.Text.Encoding.ASCII.GetString(raw, 0, end);
        foreach (string line in head.Split('\n'))
        {
            if (line.StartsWith(name + ":", System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
