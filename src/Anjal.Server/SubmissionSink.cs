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

        // Local first: if it fails for a temporary reason, nothing has been
        // queued yet, so the client's retry cannot send external copies twice.
        Anjal.Smtp.DeliveryResult? localResult = null;
        if (local.Count > 0)
        {
            localResult = await this.localSink.DeliverAsync(new Anjal.Smtp.DeliveryContext
            {
                EnvelopeFrom = ctx.EnvelopeFrom,
                EnvelopeTo = local.ToArray(),
                RawBytes = ctx.RawBytes,
                RemoteAddress = ctx.RemoteAddress,
                ClientHostName = ctx.ClientHostName,
                AuthenticatedUser = ctx.AuthenticatedUser,
                AuthResults = ctx.AuthResults,
            }, ct).ConfigureAwait(false);
            if (localResult.Outcome == Anjal.Smtp.DeliveryOutcome.TransientFailure)
            {
                return localResult;
            }
        }

        System.DateTimeOffset now = this.clock();
        foreach (string rcpt in external)
        {
            await this.outbound.EnqueueOutboundAsync(new Anjal.Store.OutboundMessage
            {
                EnvelopeFrom = ctx.EnvelopeFrom,
                EnvelopeTo = rcpt,
                RawBytes = ctx.RawBytes,
                CreatedAt = now,
                NextAttemptAt = now,
                GiveUpAt = now.AddHours(24),
            }, ct).ConfigureAwait(false);
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
            await this.mailboxes.FileSentCopyAsync(ctx.EnvelopeFrom, ctx.RawBytes, ct).ConfigureAwait(false);
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
        return this.mailboxes is not null && await this.mailboxes.ResolveAsync(recipient, ct).ConfigureAwait(false) is not null;
    }
}
