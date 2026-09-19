namespace Anjal.Mailbox;

/// <summary>
/// <see cref="Anjal.Smtp.IMessageSink"/> that delivers accepted SMTP
/// messages into tenant mailboxes. For each recipient: the domain is
/// resolved to a tenant via <c>tenant_domains</c>, the local-part (with
/// any <c>+tag</c> stripped) to a mailbox, the raw message is written to
/// the mailbox's INBOX Maildir, and a metadata row is indexed in the
/// store. Recipients with no matching mailbox are skipped so that another
/// sink (e.g. the webhook router) can claim them.
/// </summary>
public sealed class MailboxSink : Anjal.Smtp.IMessageSink
{
    private readonly Anjal.Store.IMailboxStore store;
    private readonly IMaildirStore maildir;
    private readonly System.Action<string>? log;

    /// <summary>
    /// Construct the sink.
    /// </summary>
    /// <param name="store">Mailbox registry and message index.</param>
    /// <param name="maildir">Filesystem store for message bodies.</param>
    /// <param name="log">Optional log sink. One line per delivered or skipped recipient.</param>
    public MailboxSink(Anjal.Store.IMailboxStore store, IMaildirStore maildir, System.Action<string>? log = null)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(maildir);
        this.store = store;
        this.maildir = maildir;
        this.log = log;
    }

    /// <summary>
    /// Split an address into (local-part without "+tag", domain), both
    /// lowercased. Returns <see langword="false"/> if the address has no
    /// "@" or an empty side.
    /// </summary>
    /// <param name="address">The envelope recipient.</param>
    /// <param name="localPart">The local-part with any "+tag" removed.</param>
    /// <param name="domain">The domain.</param>
    public static bool TrySplitAddress(string address, out string localPart, out string domain)
    {
        System.ArgumentNullException.ThrowIfNull(address);
        localPart = string.Empty;
        domain = string.Empty;
        int at = address.LastIndexOf('@');
        if (at <= 0 || at == address.Length - 1)
        {
            return false;
        }
        string local = address.Substring(0, at);
        int plus = local.IndexOf('+', System.StringComparison.Ordinal);
        if (plus > 0)
        {
            local = local.Substring(0, plus);
        }
        localPart = local.Trim().ToLowerInvariant();
        domain = address.Substring(at + 1).Trim().ToLowerInvariant();
        return localPart.Length > 0 && domain.Length > 0;
    }

    /// <summary>
    /// Resolve a recipient address to an enabled mailbox of an enabled
    /// tenant whose domain is registered. Returns <see langword="null"/>
    /// when any link in that chain is missing.
    /// </summary>
    /// <param name="address">The envelope recipient.</param>
    /// <param name="ct">Cancellation.</param>
    public async System.Threading.Tasks.Task<(Anjal.Store.TenantRow Tenant, Anjal.Store.MailboxRow Mailbox)?> ResolveAsync(
        string address,
        System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(address);
        if (!TrySplitAddress(address, out string local, out string domain))
        {
            return null;
        }

        Anjal.Store.TenantDomainRow? domainRow = await this.store.GetTenantDomainAsync(domain, ct).ConfigureAwait(false);
        if (domainRow is null)
        {
            return null;
        }

        Anjal.Store.TenantRow? tenant = await this.store.GetTenantByIdAsync(domainRow.TenantId, ct).ConfigureAwait(false);
        if (tenant is null || !tenant.Enabled)
        {
            return null;
        }

        Anjal.Store.MailboxRow? mailbox = await this.store.GetMailboxAsync(local, domain, ct).ConfigureAwait(false);
        if (mailbox is null || !mailbox.Enabled || mailbox.TenantId != tenant.Id)
        {
            return null;
        }

        return (tenant, mailbox);
    }

    /// <inheritdoc/>
    public async System.Threading.Tasks.Task<Anjal.Smtp.DeliveryResult> DeliverAsync(
        Anjal.Smtp.DeliveryContext ctx,
        System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.EnvelopeTo.Count == 0)
        {
            return new Anjal.Smtp.DeliveryResult
            {
                Outcome = Anjal.Smtp.DeliveryOutcome.PermanentFailure,
                ReplyText = "No recipients",
            };
        }

        Anjal.Mime.MimeMessage? parsed;
        try
        {
            parsed = Anjal.Mime.MimeParser.Parse(ctx.RawBytes);
        }
#pragma warning disable CA1031 // Intentional: an unparseable message still gets stored - the raw bytes are the truth.
        catch (System.Exception ex)
        {
            this.log?.Invoke($"Mailbox: MIME parse failed, storing with empty headers: {ex.GetType().Name}: {ex.Message}");
            parsed = null;
        }
#pragma warning restore CA1031

        int delivered = 0;
        bool transient = false;
        var seenMailboxes = new System.Collections.Generic.HashSet<System.Guid>();

        foreach (string rcpt in ctx.EnvelopeTo)
        {
            (Anjal.Store.TenantRow Tenant, Anjal.Store.MailboxRow Mailbox)? resolved =
                await this.ResolveAsync(rcpt, ct).ConfigureAwait(false);
            if (resolved is null)
            {
                this.log?.Invoke($"Mailbox: no mailbox for {rcpt}");
                continue;
            }

            (Anjal.Store.TenantRow tenant, Anjal.Store.MailboxRow mailbox) = resolved.Value;
            if (!seenMailboxes.Add(mailbox.Id))
            {
                // Same mailbox listed twice (e.g. once with a +tag) - deliver once.
                delivered++;
                continue;
            }

            try
            {
                Anjal.Store.FolderRow inbox = await this.store.EnsureFolderAsync(mailbox.Id, Anjal.Store.FolderRow.Inbox, ct).ConfigureAwait(false);
                MaildirWriteResult written = await this.maildir
                    .WriteAsync(tenant.Slug, mailbox.Address, inbox.Name, ctx.RawBytes, ct)
                    .ConfigureAwait(false);

                await this.store.SaveMessageAsync(new Anjal.Store.MessageRow
                {
                    MailboxId = mailbox.Id,
                    FolderId = inbox.Id,
                    MaildirFile = written.RelativePath,
                    EnvelopeFrom = ctx.EnvelopeFrom,
                    MessageId = parsed?.MessageId ?? string.Empty,
                    FromHeader = parsed?.Headers.Get("From") ?? string.Empty,
                    ToHeader = parsed?.Headers.Get("To") ?? string.Empty,
                    Subject = parsed?.Subject ?? string.Empty,
                    DateHeader = parsed?.Date ?? string.Empty,
                    SizeBytes = written.SizeBytes,
                }, ct).ConfigureAwait(false);

                long? used = await this.store.AddMailboxUsageAsync(mailbox.Id, written.SizeBytes, ct).ConfigureAwait(false);
                if (used is long u && mailbox.QuotaBytes > 0 && u > mailbox.QuotaBytes)
                {
                    // Soft quota: log only. Hard enforcement (reject at RCPT) is a later release.
                    this.log?.Invoke($"Mailbox: {mailbox.Address} over quota ({u} of {mailbox.QuotaBytes} bytes)");
                }

                this.log?.Invoke($"Delivered {rcpt} -> {tenant.Slug}/{mailbox.Address}/{written.RelativePath} ({written.SizeBytes} bytes)");
                delivered++;
            }
            catch (System.IO.IOException ex)
            {
                this.log?.Invoke($"Mailbox: I/O failure for {rcpt}: {ex.Message}");
                transient = true;
            }
            catch (System.UnauthorizedAccessException ex)
            {
                this.log?.Invoke($"Mailbox: permission failure for {rcpt}: {ex.Message}");
                transient = true;
            }
        }

        if (delivered > 0)
        {
            return new Anjal.Smtp.DeliveryResult
            {
                Outcome = Anjal.Smtp.DeliveryOutcome.Accepted,
                ReplyText = $"Delivered to {delivered} mailbox(es)",
            };
        }

        if (transient)
        {
            return new Anjal.Smtp.DeliveryResult
            {
                Outcome = Anjal.Smtp.DeliveryOutcome.TransientFailure,
                ReplyText = "Mailbox storage temporarily unavailable",
            };
        }

        return new Anjal.Smtp.DeliveryResult
        {
            Outcome = Anjal.Smtp.DeliveryOutcome.PermanentFailure,
            ReplyText = "No such mailbox",
        };
    }
}
