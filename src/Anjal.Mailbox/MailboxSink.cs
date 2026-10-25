namespace Anjal.Mailbox;

/// <summary>
/// <see cref="Anjal.Smtp.IMessageSink"/> that delivers accepted SMTP
/// messages into tenant mailboxes. For each recipient: the domain is
/// resolved to a tenant via <c>tenant_domains</c>, the local-part (with
/// any <c>+tag</c> stripped) to a mailbox, the raw message is written to
/// the mailbox's INBOX Maildir, and a metadata row is indexed in the
/// store. Recipients with no matching mailbox are skipped so that another
/// sink (e.g. the webhook router) can claim them.
/// <para>
/// Folder choice: a message whose <c>X-Anjal-Spam-Score</c> header (written
/// upstream by <c>Anjal.Spam.SpamFilterSink</c>) is at or above the
/// tenant's <see cref="Anjal.Store.TenantRow.SpamThreshold"/> is filed in
/// <see cref="JunkFolder"/> instead of INBOX. A tenant sender rule
/// overrides the score: allow forces INBOX, block forces Junk.
/// </para>
/// </summary>
public sealed class MailboxSink : Anjal.Smtp.IMessageSink
{
    /// <summary>Name of the folder spam is filed in.</summary>
    public const string JunkFolder = "Junk";

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

        // v1.0.0-rc.8 (RFC 5321 4.5.1, RFC 2142): postmaster@ and abuse@ are
        // always accepted. At this server's own name they reach the operator.
        if (IsRoleAddress(local) && this.ServerHostName is string host && domain.Equals(host, System.StringComparison.OrdinalIgnoreCase))
        {
            string operatorAddress = this.OperatorPostmaster ?? ("postmaster@" + ParentDomain(host));
            return TrySplitAddress(operatorAddress, out string ol, out string od) && !(IsRoleAddress(ol) && od.Equals(host, System.StringComparison.OrdinalIgnoreCase))
                ? await this.ResolveAsync(operatorAddress, ct).ConfigureAwait(false)
                : null;
        }
        (Anjal.Store.TenantRow Tenant, Anjal.Store.MailboxRow Mailbox)? direct = await this.ResolveMailboxAsync(local, domain, ct).ConfigureAwait(false);
        if (direct is not null)
        {
            return direct;
        }
        if (IsRoleAddress(local) || local.Equals(UnsubscribeLocal, System.StringComparison.OrdinalIgnoreCase))
        {
            return await this.ResolveRoleAsync(local, domain, ct).ConfigureAwait(false);
        }
        return await this.ResolveExtraAsync(local, domain, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// rc.22 (item 41): an extra address gives its mailbox; a group address gives its first member,
    /// so the address exists at RCPT TO and the webmail knows it is here. Delivery expands a group
    /// to every member with <see cref="ExpandAsync"/>.
    /// </summary>
    private async System.Threading.Tasks.Task<(Anjal.Store.TenantRow Tenant, Anjal.Store.MailboxRow Mailbox)?> ResolveExtraAsync(string local, string domain, System.Threading.CancellationToken ct)
    {
        Anjal.Store.TenantDomainRow? domainRow = await this.store.GetTenantDomainAsync(domain, ct).ConfigureAwait(false);
        Anjal.Store.TenantRow? tenant = domainRow is null ? null : await this.store.GetTenantByIdAsync(domainRow.TenantId, ct).ConfigureAwait(false);
        if (tenant is null || !tenant.Enabled)
        {
            return null;
        }
        AddressBook book = await AddressBook.ReadAsync(this.store, tenant.Id, ct).ConfigureAwait(false);
        string full = local + "@" + domain;
        System.Guid? id = book.AliasAt(full)?.MailboxId ?? book.GroupAt(full)?.Members.FirstOrDefault();
        if (id is null || id == System.Guid.Empty)
        {
            return null;
        }
        Anjal.Store.MailboxRow? m = await this.store.GetMailboxByIdAsync(id.Value, ct).ConfigureAwait(false);
        return m is null || !m.Enabled || m.TenantId != tenant.Id ? null : (tenant, m);
    }

    /// <summary>This server's own name, for postmaster@ it (v1.0.0-rc.8); null to not recognise it.</summary>
    public string? ServerHostName { get; init; }

    /// <summary>The operator's mailbox for postmaster@ this server; null for postmaster@ its parent domain.</summary>
    public string? OperatorPostmaster { get; init; }

    /// <summary>The virus scanner for files on mail from outside (rc.22, item 39); null when scanning is off.</summary>
    public IVirusScanner? Scanner { get; init; }

    /// <summary>The header a forwarded copy carries (rc.22, item 29): each mailbox it passed, so a loop stops.</summary>
    public const string ForwardedHeader = "X-Anjal-Forwarded";

    /// <summary>The most mailboxes a copy is forwarded through in a chain.</summary>
    public const int MaxForwardHops = 3;

    /// <summary>The audit action written for every forward (rc.22, item 29: forwards are logged).</summary>
    public const string ForwardAudit = "webmail.forwarded";

    /// <summary>The mailbox document counting mail discarded from blocked senders (rc.22, item 32), by sender.</summary>
    public const string DiscardsKind = "discards";

    /// <summary>
    /// Whether a message is automatic - a list, a bounce, an auto-reply or any other machine mail -
    /// which is never answered automatically and never forwarded by a rule (RFC 3834).
    /// </summary>
    /// <param name="headers">The message's headers.</param>
    /// <returns>True for automatic mail.</returns>
    public static bool IsAutomaticMail(Anjal.Mime.HeaderCollection headers)
    {
        System.ArgumentNullException.ThrowIfNull(headers);
        string auto = (headers.Get("Auto-Submitted") ?? "no").Trim();
        if (!auto.StartsWith("no", System.StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (headers.Contains("List-Id") || headers.Contains("List-Unsubscribe") || headers.Contains("X-Auto-Response-Suppress"))
        {
            return true;
        }
        string precedence = (headers.Get("Precedence") ?? string.Empty).Trim();
        return precedence.Equals("bulk", System.StringComparison.OrdinalIgnoreCase)
            || precedence.Equals("list", System.StringComparison.OrdinalIgnoreCase)
            || precedence.Equals("junk", System.StringComparison.OrdinalIgnoreCase)
            || precedence.Equals("auto_reply", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every mailbox an address reaches (rc.22, item 41): the mailbox itself, the mailbox an extra
    /// address delivers to, or each member of a group address. Empty when the address is not here.
    /// A group that takes mail only from its organisation gives no mailboxes to a sender outside it,
    /// and says so.
    /// </summary>
    /// <param name="address">The envelope recipient.</param>
    /// <param name="senderDomain">The sender's domain, for a group's "only the organisation" rule; empty for a signed-in sender.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The mailboxes, and a refusal in words when a group refused the sender.</returns>
    public async System.Threading.Tasks.Task<(IReadOnlyList<(Anjal.Store.TenantRow Tenant, Anjal.Store.MailboxRow Mailbox)> Targets, string? Refusal)> ExpandAsync(string address, string senderDomain, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(address);
        System.ArgumentNullException.ThrowIfNull(senderDomain);
        var targets = new System.Collections.Generic.List<(Anjal.Store.TenantRow, Anjal.Store.MailboxRow)>();
        // The address book first: ResolveAsync answers a group with its first member (so RCPT TO
        // accepts it), but delivery must reach every member and honour "organisation only".
        if (!TrySplitAddress(address, out string local, out string domain))
        {
            return (targets, null);
        }
        Anjal.Store.TenantDomainRow? domainRow = await this.store.GetTenantDomainAsync(domain, ct).ConfigureAwait(false);
        Anjal.Store.TenantRow? tenant = domainRow is null ? null : await this.store.GetTenantByIdAsync(domainRow.TenantId, ct).ConfigureAwait(false);
        if (tenant is null || !tenant.Enabled)
        {
            (Anjal.Store.TenantRow Tenant, Anjal.Store.MailboxRow Mailbox)? elsewhere = await this.ResolveAsync(address, ct).ConfigureAwait(false);
            if (elsewhere is not null)
            {
                targets.Add(elsewhere.Value);
            }
            return (targets, null);
        }
        AddressBook book = await AddressBook.ReadAsync(this.store, tenant.Id, ct).ConfigureAwait(false);
        string full = local.ToLowerInvariant() + "@" + domain.ToLowerInvariant();
        if (book.AliasAt(full) is null && book.GroupAt(full) is null)
        {
            (Anjal.Store.TenantRow Tenant, Anjal.Store.MailboxRow Mailbox)? direct = await this.ResolveAsync(address, ct).ConfigureAwait(false);
            if (direct is not null)
            {
                targets.Add(direct.Value);
            }
            return (targets, null);
        }
        if (book.AliasAt(full) is AliasEntry alias)
        {
            Anjal.Store.MailboxRow? m = await this.store.GetMailboxByIdAsync(alias.MailboxId, ct).ConfigureAwait(false);
            if (m is not null && m.Enabled && m.TenantId == tenant.Id)
            {
                targets.Add((tenant, m));
            }
            return (targets, null);
        }
        if (book.GroupAt(full) is GroupEntry group)
        {
            if (group.OrgOnly && senderDomain.Length > 0)
            {
                bool inside = false;
                foreach (Anjal.Store.TenantDomainRow d in await this.store.ListTenantDomainsAsync(tenant.Id, ct).ConfigureAwait(false))
                {
                    inside |= string.Equals(d.Domain, senderDomain, System.StringComparison.OrdinalIgnoreCase);
                }
                if (!inside)
                {
                    return (targets, $"{full} takes mail only from its own organisation");
                }
            }
            foreach (System.Guid id in group.Members)
            {
                Anjal.Store.MailboxRow? m = await this.store.GetMailboxByIdAsync(id, ct).ConfigureAwait(false);
                if (m is not null && m.Enabled && m.TenantId == tenant.Id)
                {
                    targets.Add((tenant, m));
                }
            }
        }
        return (targets, null);
    }

    /// <summary>
    /// The message as filed at final delivery (v1.0.0-rc.9, DEF-069; RFC 5321 4.4):
    /// a Return-Path line with the envelope sender first, after removing any
    /// Return-Path the sender included (the standard allows it; only the final
    /// server knows the real envelope - D-55). The evidence copy is untouched.
    /// </summary>
    /// <param name="raw">The message.</param>
    /// <param name="envelopeFrom">The envelope sender; empty for bounces, giving &lt;&gt;.</param>
    /// <returns>The bytes to file.</returns>
    public static byte[] WithReturnPath(byte[] raw, string? envelopeFrom)
    {
        System.ArgumentNullException.ThrowIfNull(raw);
        var kept = new System.IO.MemoryStream(raw.Length + 64);
        int pos = 0;
        bool inHeader = true;
        while (pos < raw.Length)
        {
            int nl = System.Array.IndexOf(raw, (byte)'\n', pos);
            int end = nl < 0 ? raw.Length : nl + 1;
            if (inHeader && (raw[pos] == (byte)'\r' || raw[pos] == (byte)'\n'))
            {
                inHeader = false;
            }
            bool drop = false;
            if (inHeader && end - pos >= 12 && raw[pos] != (byte)' ' && raw[pos] != (byte)'\t'
                && System.Text.Encoding.ASCII.GetString(raw, pos, 12).Equals("Return-Path:", System.StringComparison.OrdinalIgnoreCase))
            {
                drop = true;
                // Also drop its folded continuation lines.
                while (end < raw.Length && (raw[end] == (byte)' ' || raw[end] == (byte)'\t'))
                {
                    int n2 = System.Array.IndexOf(raw, (byte)'\n', end);
                    end = n2 < 0 ? raw.Length : n2 + 1;
                }
            }
            if (!drop)
            {
                kept.Write(raw, pos, end - pos);
            }
            pos = end;
        }
        byte[] line = System.Text.Encoding.UTF8.GetBytes("Return-Path: <" + (envelopeFrom ?? string.Empty) + ">\r\n");
        byte[] body = kept.ToArray();
        var result = new byte[line.Length + body.Length];
        line.CopyTo(result, 0);
        body.CopyTo(result, line.Length);
        return result;
    }

    /// <summary>Whether a local part is one every mail domain must accept (postmaster, abuse).</summary>
    /// <param name="local">The local part.</param>
    /// <returns>True for postmaster and abuse, in any case.</returns>
    public static bool IsRoleAddress(string local)
    {
        System.ArgumentNullException.ThrowIfNull(local);
        return local.Equals("postmaster", System.StringComparison.OrdinalIgnoreCase) || local.Equals("abuse", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One line for the start-up log: where postmaster@ and abuse@ at this
    /// server's own name are delivered (v1.0.0-rc.10, DEF-082). Built from the
    /// same rule the delivery uses, so the two cannot disagree.
    /// </summary>
    /// <returns>The sentence.</returns>
    public string DescribeServerRoleAddresses()
    {
        if (string.IsNullOrEmpty(this.ServerHostName))
        {
            return "Postmaster: no server name set; postmaster@ and abuse@ go to each domain's designated mailbox.";
        }
        string host = this.ServerHostName;
        return this.OperatorPostmaster is string op
            ? $"Postmaster: postmaster@ and abuse@ at {host} go to {op} (ANJAL_POSTMASTER)."
            : $"Postmaster: postmaster@ and abuse@ at {host} go to postmaster@{ParentDomain(host)} (the parent domain; ANJAL_POSTMASTER not set), then to that domain's designated mailbox.";
    }

    private static string ParentDomain(string host)
    {
        int dot = host.IndexOf('.', System.StringComparison.Ordinal);
        return dot > 0 && host.IndexOf('.', dot + 1) > 0 ? host[(dot + 1)..] : host;
    }

    private async System.Threading.Tasks.Task<(Anjal.Store.TenantRow Tenant, Anjal.Store.MailboxRow Mailbox)?> ResolveRoleAsync(string local, string domain, System.Threading.CancellationToken ct)
    {
        Anjal.Store.TenantDomainRow? domainRow = await this.store.GetTenantDomainAsync(domain, ct).ConfigureAwait(false);
        Anjal.Store.TenantRow? tenant = domainRow is null ? null : await this.store.GetTenantByIdAsync(domainRow.TenantId, ct).ConfigureAwait(false);
        if (tenant is null || !tenant.Enabled)
        {
            return null;
        }
        if (tenant.PostmasterMailbox is string designated && TrySplitAddress(designated, out string dl, out string dd))
        {
            (Anjal.Store.TenantRow Tenant, Anjal.Store.MailboxRow Mailbox)? target = await this.ResolveMailboxAsync(dl, dd, ct).ConfigureAwait(false);
            if (target is not null && target.Value.Tenant.Id == tenant.Id)
            {
                return target;
            }
            this.log?.Invoke($"Mailbox: {local}@{domain} - designated mailbox {designated} is not an enabled mailbox of tenant {tenant.Slug}; using its first mailbox.");
        }
        Anjal.Store.MailboxRow? first = (await this.store.ListMailboxesAsync(tenant.Id, ct).ConfigureAwait(false))
            .Where(m => m.Enabled).OrderBy(m => m.Address, System.StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (first is null)
        {
            return null;
        }
        if (tenant.PostmasterMailbox is null)
        {
            this.log?.Invoke($"Mailbox: {local}@{domain} delivered to {first.Address}, the tenant's first mailbox - designate one with postmasterMailbox (POST /api/tenants, slug {tenant.Slug}).");
        }
        return (tenant, first);
    }

    private async System.Threading.Tasks.Task<(Anjal.Store.TenantRow Tenant, Anjal.Store.MailboxRow Mailbox)?> ResolveMailboxAsync(string local, string domain, System.Threading.CancellationToken ct)
    {
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

    /// <summary>
    /// Decide the destination folder: a sender rule wins outright; otherwise
    /// the score is compared with the tenant threshold (a threshold of 0 or
    /// less disables junk filing for the tenant).
    /// </summary>
    /// <param name="spamScore">Score from the <c>X-Anjal-Spam-Score</c> header, 0 if none.</param>
    /// <param name="threshold">Tenant threshold.</param>
    /// <param name="rule">Matching sender rule, if any.</param>
    public static string ChooseFolder(int spamScore, int threshold, Anjal.Store.SenderRuleAction? rule)
    {
        if (rule == Anjal.Store.SenderRuleAction.Allow)
        {
            return Anjal.Store.FolderRow.Inbox;
        }
        if (rule == Anjal.Store.SenderRuleAction.Block)
        {
            return JunkFolder;
        }
        return threshold > 0 && spamScore >= threshold ? JunkFolder : Anjal.Store.FolderRow.Inbox;
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

        Anjal.Mime.MimeMessage? parsed = this.ParseQuietly(ctx.RawBytes);
        byte[] raw = ctx.RawBytes;

        // rc.22 (item 39, decisions of 2 Oct 2026): the files on mail from outside, before anything
        // else. Programs and scripts are refused outright; a virus is removed and the message
        // delivered without it; a disk image goes to Junk; with the scanner down, the mail is held.
        FileJudgement files = ctx.AuthenticatedUser is null
            ? await AttachmentPolicy.JudgeAsync(parsed, this.Scanner, ct).ConfigureAwait(false)
            : new FileJudgement(FileVerdict.Clean, System.Array.Empty<string>(), string.Empty, null, 0);
        if (files.Scanned > 0 || files.Verdict != FileVerdict.Clean)
        {
            await this.CountFilesAsync(files, ct).ConfigureAwait(false);
        }
        if (files.Verdict == FileVerdict.Refused)
        {
            this.log?.Invoke($"Mailbox: refused, programs or scripts attached: {string.Join(", ", files.Files)}");
            Anjal.Smtp.Counters.Increment("anjal_virus_refused_total");
            return new Anjal.Smtp.DeliveryResult
            {
                Outcome = Anjal.Smtp.DeliveryOutcome.PermanentFailure,
                ReplyText = "Programs and scripts are not accepted (" + string.Join(", ", files.Files) + "); send them another way",
            };
        }
        if (files.Verdict == FileVerdict.Removed && files.RawBytes is not null)
        {
            raw = files.RawBytes;
            parsed = this.ParseQuietly(raw);
            this.log?.Invoke($"Mailbox: virus removed: {string.Join(", ", files.Files)} ({files.Detail})");
            Anjal.Smtp.Counters.Increment("anjal_virus_removed_total");
        }

        // rc.24 (item 94, owner 25 Oct 2026: "scan the whole mail, including links"): the whole message -
        // its text and HTML as well as what is left of its files - goes to ClamAV once more, whose phishing
        // checks and signatures read links. A hit is delivered with a clear warning and its links switched
        // off in the reader (X-Anjal-Mail-Warning). The header is Anjal's alone: one arriving from outside
        // is removed first.
        if (ctx.AuthenticatedUser is null)
        {
            string? mailWarning = null;
            if (this.Scanner is not null && files.Verdict != FileVerdict.Held)
            {
                ScanResult whole = await this.Scanner.ScanAsync(raw, ct).ConfigureAwait(false);
                if (whole.Verdict == ScanVerdict.Infected)
                {
                    mailWarning = whole.Name.Length > 0 ? whole.Name : "dangerous content";
                    this.log?.Invoke($"Mailbox: the whole mail was flagged ({mailWarning}); delivered with a warning, links off");
                    Anjal.Smtp.Counters.Increment("anjal_mail_warned_total");
                }
            }
            byte[] marked = AttachmentPolicy.MarkWholeMail(raw, mailWarning);
            if (!ReferenceEquals(marked, raw))
            {
                raw = marked;
                parsed = this.ParseQuietly(raw);
            }
        }

        int spamScore = Anjal.Spam.SpamHeaders.ScoreOf(parsed);

        // Decision 1B: the spam filter scores only mail from outside (no
        // signed-in user) and writes its score header on every message it
        // scores. A message from a signed-in account is not checked, whatever
        // headers it carries.
        bool spamChecked = ctx.AuthenticatedUser is null && parsed?.Headers.Get(Anjal.Spam.SpamHeaders.Score) is not null;
        string fromHeaderAddress = parsed is null ? string.Empty : Anjal.Spam.SpamScorer.FirstAddress(parsed.Headers.Get("From") ?? string.Empty);
        string senderDomain = ctx.AuthenticatedUser is not null ? string.Empty : Anjal.Spam.SpamScorer.DomainOf(ctx.EnvelopeFrom.Length > 0 ? ctx.EnvelopeFrom : fromHeaderAddress);
        var rulesByTenant = new System.Collections.Generic.Dictionary<System.Guid, System.Collections.Generic.IReadOnlyList<Anjal.Store.SenderRuleRow>>();

        int delivered = 0;
        bool transient = false;
        string? refusal = null;
        var seenMailboxes = new System.Collections.Generic.HashSet<System.Guid>();
        var heldFor = new System.Collections.Generic.List<string>();

        foreach (string rcpt in ctx.EnvelopeTo)
        {
            // rc.22 (item 51): unsubscribe@ an organisation's domain, the address applications' mail
            // offers in List-Unsubscribe, puts the sender on the organisation's do-not-send list.
            if (await this.UnsubscribeAsync(rcpt, ctx.EnvelopeFrom, fromHeaderAddress, parsed, ct).ConfigureAwait(false))
            {
                delivered++;
                continue;
            }
            (IReadOnlyList<(Anjal.Store.TenantRow Tenant, Anjal.Store.MailboxRow Mailbox)> targets, string? refused) =
                await this.ExpandAsync(rcpt, senderDomain, ct).ConfigureAwait(false);
            if (refused is not null)
            {
                this.log?.Invoke($"Mailbox: {rcpt} refused the sender: {refused}");
                refusal ??= refused;
                continue;
            }
            if (targets.Count == 0)
            {
                this.log?.Invoke($"Mailbox: no mailbox for {rcpt}");
                continue;
            }

            foreach ((Anjal.Store.TenantRow tenant, Anjal.Store.MailboxRow mailbox) in targets)
            {
                if (!seenMailboxes.Add(mailbox.Id))
                {
                    // Same mailbox listed twice (e.g. once with a +tag, or through a group) - deliver once.
                    delivered++;
                    continue;
                }

                // DES-11 D2 (owner, 10 Oct 2026): a full mailbox receives nothing - the organisation's
                // storage plan taken in - except Anjal's own security mail. Deferred, so a sender retries.
                if (!ctx.AnjalSecurityMail && (await Anjal.Store.StoragePlan.StateOfAsync(this.store, mailbox, ct).ConfigureAwait(false)).Full)
                {
                    this.log?.Invoke($"Mailbox: {mailbox.Address} is full; not delivered");
                    Anjal.Smtp.Counters.Increment("anjal_quota_refusals_total");
                    transient = true;
                    continue;
                }

                try
                {
                    if (!rulesByTenant.TryGetValue(tenant.Id, out System.Collections.Generic.IReadOnlyList<Anjal.Store.SenderRuleRow>? rules))
                    {
                        rules = await this.store.ListSenderRulesAsync(tenant.Id, ct).ConfigureAwait(false);
                        rulesByTenant[tenant.Id] = rules;
                    }
                    // The tenant's rules (set by an administrator) and this
                    // mailbox's own (from its Report spam / Not spam / Block) are judged
                    // together: an exact address beats a domain, discard beats block beats allow.
                    IReadOnlyList<Anjal.Store.SenderRuleRow> personal = await this.store.ListMailboxSenderRulesAsync(mailbox.Id, ct).ConfigureAwait(false);
                    IReadOnlyList<Anjal.Store.SenderRuleRow> effective = personal.Count == 0
                        ? rules
                        : new System.Collections.Generic.List<Anjal.Store.SenderRuleRow>(rules.Concat(personal));
                    Anjal.Store.SenderRuleAction? senderRule = Anjal.Spam.SenderRules.Evaluate(effective, ctx.EnvelopeFrom, fromHeaderAddress);

                    // rc.22 (item 32, owner 18 Oct 2026, option (b)): a blocked sender's mail is
                    // discarded silently - no folder sees it, the sender is not told. Accepted on the
                    // wire, so the as-received evidence copy is kept like every other message.
                    if (senderRule == Anjal.Store.SenderRuleAction.Discard)
                    {
                        await this.CountDiscardAsync(mailbox.Id, fromHeaderAddress.Length > 0 ? fromHeaderAddress : ctx.EnvelopeFrom, ct).ConfigureAwait(false);
                        this.log?.Invoke($"Mailbox: {mailbox.Address} discarded mail from a blocked sender ({ctx.EnvelopeFrom})");
                        Anjal.Smtp.Counters.Increment("anjal_mailbox_discarded_total");
                        delivered++;
                        continue;
                    }

                    string folderName = files.Verdict == FileVerdict.Held ? AttachmentPolicy.QuarantineFolder
                        : files.Verdict == FileVerdict.Junk && senderRule != Anjal.Store.SenderRuleAction.Allow ? JunkFolder
                        : ChooseFolder(spamScore, tenant.SpamThreshold, senderRule);

                    // Unencrypted mail from outside (v1.0.0-rc.7): filed in the
                    // tenant's chosen folder, if it has one, unless the recipient
                    // trusts the sender. Junk decisions come first.
                    if (ctx.AuthenticatedUser is null && ctx.TransportTls is null
                        && !string.IsNullOrWhiteSpace(tenant.UnencryptedFolder)
                        && string.Equals(folderName, "INBOX", System.StringComparison.OrdinalIgnoreCase)
                        && !await this.IsTrustedAsync(mailbox.Id, fromHeaderAddress, ctx.EnvelopeFrom, ct).ConfigureAwait(false))
                    {
                        folderName = tenant.UnencryptedFolder.Trim();
                    }

                    // rc.12 (items 24, 31): the person's own rules, for mail on its way to INBOX.
                    // Junk stays Junk: a rule never brings mail out of it.
                    RuleOutcome? ruled = null;
                    if (parsed is not null && string.Equals(folderName, Anjal.Store.FolderRow.Inbox, System.StringComparison.OrdinalIgnoreCase))
                    {
                        ruled = await this.RuleForAsync(tenant, mailbox, parsed, fromHeaderAddress, ct).ConfigureAwait(false);
                        if (ruled is not null && IsRuleTarget(ruled.MoveTo))
                        {
                            folderName = ruled.MoveTo;
                        }
                    }

                    Anjal.Store.FolderRow folder = await this.store.EnsureFolderAsync(mailbox.Id, folderName, ct).ConfigureAwait(false);
                    MaildirWriteResult written = await this.maildir
                        .WriteAsync(tenant.Slug, mailbox.Address, folder.Name, WithReturnPath(raw, ctx.EnvelopeFrom), ct)
                        .ConfigureAwait(false);
                    string storedFile = written.RelativePath;
                    if (ruled is not null && (ruled.MarkRead || ruled.Flag))
                    {
                        storedFile = this.maildir.SetFlags(tenant.Slug, mailbox.Address, folder.Name, written.RelativePath, seen: ruled.MarkRead, flagged: ruled.Flag, answered: false) ?? written.RelativePath;
                    }

                    await this.store.SaveMessageAsync(new Anjal.Store.MessageRow
                    {
                        MailboxId = mailbox.Id,
                        FolderId = folder.Id,
                        MaildirFile = storedFile,
                        Seen = ruled?.MarkRead == true,
                        Flagged = ruled?.Flag == true,
                        EnvelopeFrom = ctx.EnvelopeFrom,
                        MessageId = parsed?.MessageId ?? string.Empty,
                        FromHeader = parsed?.Headers.Get("From") ?? string.Empty,
                        ToHeader = parsed?.Headers.Get("To") ?? string.Empty,
                        Subject = parsed?.Subject ?? string.Empty,
                        DateHeader = parsed?.Date ?? string.Empty,
                        SizeBytes = written.SizeBytes,
                        SpamScore = spamScore,
                        SpamChecked = spamChecked,
                        TransportEncrypted = ctx.AuthenticatedUser is null ? ctx.TransportTls is not null : null,
                        TransportTls = ctx.AuthenticatedUser is null ? ctx.TransportTls : null,
                        EvidenceId = ctx.EvidenceId,
                        HasAttachments = HasAttachment(parsed?.Body),
                        BodyText = MessageText.Extract(parsed),
                        CategoryId = await this.CategoryForAsync(mailbox.Id, ctx.EnvelopeFrom, parsed?.Headers.Get("From") ?? string.Empty, ct).ConfigureAwait(false),
                    }, ct).ConfigureAwait(false);

                    // v1.0.0-rc.8: the evidence is kept at least as long as this tenant requires.
                    if (ctx.EvidenceId is System.Guid evidenceId && this.store is Anjal.Store.IEvidenceStore evidenceStore)
                    {
                        await evidenceStore.RaiseEvidenceRetentionAsync(evidenceId, tenant.EvidenceRetentionDays, ct).ConfigureAwait(false);
                    }

                    long? used = await this.store.AddMailboxUsageAsync(mailbox.Id, written.SizeBytes, ct).ConfigureAwait(false);
                    if (used is long u && mailbox.QuotaBytes > 0 && u > mailbox.QuotaBytes)
                    {
                        // The message that filled it was let in whole; the next is refused (DES-11 D2).
                        this.log?.Invoke($"Mailbox: {mailbox.Address} is now full ({u} of {mailbox.QuotaBytes} bytes)");
                    }

                    this.log?.Invoke($"Delivered {rcpt} -> {tenant.Slug}/{mailbox.Address}/{Anjal.Store.FolderRow.MaildirNameFor(folder.Name)}/{written.RelativePath} ({written.SizeBytes} bytes, spam score {spamScore})");
                    Anjal.Smtp.Counters.Increment(folderName == JunkFolder ? "anjal_mailbox_junked_total" : "anjal_mailbox_delivered_total");
                    Anjal.Smtp.Counters.Add("anjal_mailbox_bytes_stored_total", written.SizeBytes);
                    delivered++;

                    if (files.Verdict == FileVerdict.Held)
                    {
                        heldFor.Add(mailbox.Address);
                        continue;
                    }

                    // rc.22 (item 29): a rule that forwards sends a copy on; the original stays above.
                    if (ruled is not null && ruled.ForwardTo.Length > 0 && parsed is not null)
                    {
                        await this.ForwardAsync(tenant, mailbox, ruled.ForwardTo, raw, parsed, ctx, ct).ConfigureAwait(false);
                    }

                    // rc.22 (item 30): the out-of-office reply, once per sender, never to automatic mail.
                    if (parsed is not null && !ctx.AnjalSecurityMail && folderName != JunkFolder)
                    {
                        await this.AwayReplyAsync(tenant, mailbox, parsed, fromHeaderAddress, ctx, ct).ConfigureAwait(false);
                    }
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
        }

        if (heldFor.Count > 0)
        {
            await this.HeldNoticeAsync(heldFor, ctx, files, ct).ConfigureAwait(false);
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
            ReplyText = refusal ?? "No such mailbox",
        };
    }

    /// <summary>The local part that takes unsubscribe requests for an organisation's applications (rc.22, item 51).</summary>
    public const string UnsubscribeLocal = "unsubscribe";

    private async System.Threading.Tasks.Task<bool> UnsubscribeAsync(string rcpt, string envelopeFrom, string fromHeaderAddress, Anjal.Mime.MimeMessage? parsed, System.Threading.CancellationToken ct)
    {
        if (!TrySplitAddress(rcpt, out string local, out string domain) || !local.Equals(UnsubscribeLocal, System.StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (await this.store.GetMailboxAsync(local, domain, ct).ConfigureAwait(false) is not null)
        {
            // Someone made a real mailbox called unsubscribe@: it is theirs.
            return false;
        }
        Anjal.Store.TenantDomainRow? domainRow = await this.store.GetTenantDomainAsync(domain, ct).ConfigureAwait(false);
        if (domainRow is null)
        {
            return false;
        }
        var who = new System.Collections.Generic.List<string>();
        foreach (string a in new[] { envelopeFrom, fromHeaderAddress })
        {
            if (TrySplitAddress(a, out string l, out string d) && !who.Contains(l + "@" + d))
            {
                who.Add(l + "@" + d);
            }
        }
        // The subject may name the address being unsubscribed (List-Unsubscribe: ...?subject=unsubscribe%20addr).
        string subject = parsed?.Subject ?? string.Empty;
        foreach (string word in subject.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
        {
            if (TrySplitAddress(word.Trim('<', '>', ',', ';'), out string l, out string d) && !who.Contains(l + "@" + d))
            {
                who.Add(l + "@" + d);
            }
        }
        if (who.Count == 0)
        {
            return true;
        }
        Anjal.Store.DoNotSendDocument doc = await Anjal.Store.DoNotSend.ReadAsync(this.store, domainRow.TenantId, ct).ConfigureAwait(false);
        System.DateTimeOffset now = System.DateTimeOffset.UtcNow;
        bool changed = false;
        foreach (string address in who)
        {
            if (!doc.Entries.Any(e => string.Equals(e.Address, address, System.StringComparison.OrdinalIgnoreCase)))
            {
                doc.Entries.Add(new Anjal.Store.DoNotSendEntry { Address = address, Reason = "Unsubscribed", Added = now, By = Anjal.Store.DoNotSend.ByAnjal });
                changed = true;
            }
        }
        if (changed)
        {
            await Anjal.Store.DoNotSend.WriteAsync(this.store, domainRow.TenantId, doc, ct).ConfigureAwait(false);
        }
        this.log?.Invoke($"Mailbox: {rcpt}: {string.Join(", ", who)} unsubscribed from the organisation's applications");
        Anjal.Smtp.Counters.Increment("anjal_unsubscribe_total");
        return true;
    }

    /// <summary>Parse a message, or null when it cannot be parsed: the raw bytes are the truth and are stored anyway.</summary>
    private Anjal.Mime.MimeMessage? ParseQuietly(byte[] raw)
    {
        try
        {
            return Anjal.Mime.MimeParser.Parse(raw);
        }
#pragma warning disable CA1031 // Intentional: an unparseable message still gets stored - the raw bytes are the truth.
        catch (System.Exception ex)
        {
            this.log?.Invoke($"Mailbox: MIME parse failed, storing with empty headers: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
#pragma warning restore CA1031
    }

    private async System.Threading.Tasks.Task CountFilesAsync(FileJudgement files, System.Threading.CancellationToken ct)
    {
        try
        {
            await VirusStats.UpdateAsync(this.store, stats =>
            {
                stats.Scanned += files.Scanned;
                switch (files.Verdict)
                {
                    case FileVerdict.Refused:
                        stats.Refused++;
                        break;
                    case FileVerdict.Junk:
                        stats.Junked++;
                        break;
                    case FileVerdict.Removed:
                        stats.Removed++;
                        break;
                    case FileVerdict.Held:
                        stats.Held++;
                        stats.LastDown = System.DateTimeOffset.UtcNow;
                        break;
                    default:
                        break;
                }
                if (files.Scanned > 0)
                {
                    stats.LastSeen = System.DateTimeOffset.UtcNow;
                }
            }, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The count is a convenience; delivery must not fail for it.
        catch (System.Exception ex)
        {
            this.log?.Invoke($"Mailbox: virus counts not updated: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    private async System.Threading.Tasks.Task CountDiscardAsync(System.Guid mailboxId, string sender, System.Threading.CancellationToken ct)
    {
        try
        {
            System.Collections.Generic.Dictionary<string, int> counts = MailDocuments.Parse<System.Collections.Generic.Dictionary<string, int>>(
                await this.store.GetMailboxDocumentAsync(mailboxId, DiscardsKind, ct).ConfigureAwait(false)) ?? new System.Collections.Generic.Dictionary<string, int>();
            var kept = new System.Collections.Generic.Dictionary<string, int>(counts, System.StringComparer.OrdinalIgnoreCase);
            string key = sender.Trim().ToLowerInvariant();
            kept[key] = kept.TryGetValue(key, out int n) ? n + 1 : 1;
            await this.store.SetMailboxDocumentAsync(mailboxId, DiscardsKind, MailDocuments.Write(kept), ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The count is a convenience.
        catch (System.Exception ex)
        {
            this.log?.Invoke($"Mailbox: discard count not updated: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// rc.22 (item 29, owner 18 Oct 2026): send a copy on. Always keeps the original; only by rule;
    /// outside the organisation only when its administrator allows it (individuals may forward
    /// anywhere); every forward is written to the audit trail. Automatic mail and bounces are never
    /// forwarded, and a copy stops after three mailboxes, so two rules cannot chase each other.
    /// </summary>
    private async System.Threading.Tasks.Task ForwardAsync(Anjal.Store.TenantRow tenant, Anjal.Store.MailboxRow mailbox, string target, byte[] raw, Anjal.Mime.MimeMessage parsed, Anjal.Smtp.DeliveryContext ctx, System.Threading.CancellationToken ct)
    {
        if (!TrySplitAddress(target, out string tl, out string td) || string.Equals(target, mailbox.Address, System.StringComparison.OrdinalIgnoreCase)
            || ctx.EnvelopeFrom.Length == 0 || IsAutomaticMail(parsed.Headers))
        {
            return;
        }
        IReadOnlyList<string> hops = parsed.Headers.GetAll(ForwardedHeader);
        if (hops.Count >= MaxForwardHops || hops.Any(h => h.Contains(mailbox.Address, System.StringComparison.OrdinalIgnoreCase)))
        {
            this.log?.Invoke($"Mailbox: not forwarded again from {mailbox.Address}: already passed {hops.Count} mailbox(es)");
            return;
        }
        string targetAddress = tl + "@" + td;
        bool outside = true;
        foreach (Anjal.Store.TenantDomainRow d in await this.store.ListTenantDomainsAsync(tenant.Id, ct).ConfigureAwait(false))
        {
            outside &= !string.Equals(d.Domain, td, System.StringComparison.OrdinalIgnoreCase);
        }
        if (outside && !await TenantKind.IsIndividualsAsync(this.store, tenant.Id, ct).ConfigureAwait(false)
            && !(await OrgMailPolicy.ReadAsync(this.store, tenant.Id, ct).ConfigureAwait(false)).ForwardingOutside)
        {
            this.log?.Invoke($"Mailbox: {mailbox.Address} may not forward outside {tenant.Slug}; copy to {targetAddress} not sent");
            return;
        }
        string now = System.DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", System.Globalization.CultureInfo.InvariantCulture);
        string head = "Resent-From: <" + mailbox.Address + ">\r\n"
            + "Resent-To: <" + targetAddress + ">\r\n"
            + "Resent-Date: " + now + "\r\n"
            + "Resent-Message-ID: <" + System.Guid.NewGuid().ToString("N") + "@" + mailbox.Domain + ">\r\n"
            + ForwardedHeader + ": " + mailbox.Address + "\r\n";
        byte[] copy = Prepend(System.Text.Encoding.ASCII.GetBytes(head), StripReturnPath(raw));
        await this.SendOnAsync(mailbox.Address, targetAddress, copy, ct).ConfigureAwait(false);
        if (this.store is Anjal.Store.IMessageStore audit)
        {
            await audit.AppendAuditAsync(new Anjal.Store.AuditEvent
            {
                Actor = mailbox.Address,
                Action = ForwardAudit,
                Subject = targetAddress,
                Detail = Clip(parsed.Subject, 120),
            }, ct).ConfigureAwait(false);
        }
        this.log?.Invoke($"Mailbox: {mailbox.Address} forwarded a copy to {targetAddress}");
        Anjal.Smtp.Counters.Increment("anjal_mailbox_forwarded_total");
    }

    /// <summary>
    /// rc.22 (item 30): the out-of-office reply. Once per sender while it is on; never to
    /// automatic mail, lists, no-reply addresses or the mailbox itself; outside the organisation
    /// only when the organisation and the person both allow it.
    /// </summary>
    private async System.Threading.Tasks.Task AwayReplyAsync(Anjal.Store.TenantRow tenant, Anjal.Store.MailboxRow mailbox, Anjal.Mime.MimeMessage parsed, string fromHeaderAddress, Anjal.Smtp.DeliveryContext ctx, System.Threading.CancellationToken ct)
    {
        System.DateTimeOffset now = System.DateTimeOffset.UtcNow;
        AwayReply away = await AwayReply.ReadAsync(this.store, mailbox.Id, ct).ConfigureAwait(false);
        if (!away.IsActive(now))
        {
            return;
        }
        string sender = (fromHeaderAddress.Length > 0 ? fromHeaderAddress : ctx.EnvelopeFrom).Trim().ToLowerInvariant();
        if (ctx.EnvelopeFrom.Length == 0 || away.Skips(sender, parsed.Headers) || string.Equals(sender, mailbox.Address, System.StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (away.Replied.TryGetValue(sender, out System.DateTimeOffset when) && when >= (away.From ?? System.DateTimeOffset.MinValue))
        {
            return;
        }
        string senderDomain = Anjal.Spam.SpamScorer.DomainOf(sender);
        bool outside = true;
        foreach (Anjal.Store.TenantDomainRow d in await this.store.ListTenantDomainsAsync(tenant.Id, ct).ConfigureAwait(false))
        {
            outside &= !string.Equals(d.Domain, senderDomain, System.StringComparison.OrdinalIgnoreCase);
        }
        if (outside)
        {
            if (away.ReplyTo == "org" || (!(await OrgMailPolicy.ReadAsync(this.store, tenant.Id, ct).ConfigureAwait(false)).AwayOutside
                && !await TenantKind.IsIndividualsAsync(this.store, tenant.Id, ct).ConfigureAwait(false)))
            {
                return;
            }
            if (away.ReplyTo == "contacts" && !await this.IsContactAsync(mailbox.Id, sender, ct).ConfigureAwait(false))
            {
                return;
            }
        }
        string subject = away.Subject.Trim().Length > 0 ? away.Subject.Trim() : "Automatic reply: " + parsed.Subject;
        string messageId = parsed.Headers.Get("Message-ID")?.Trim() ?? string.Empty;
        var sb = new System.Text.StringBuilder();
        sb.Append("From: ").Append(FromLine(mailbox)).Append("\r\n");
        sb.Append("To: <").Append(sender).Append(">\r\n");
        sb.Append("Subject: ").Append(EncodeHeader(subject)).Append("\r\n");
        sb.Append("Date: ").Append(now.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("Message-ID: <").Append(System.Guid.NewGuid().ToString("N")).Append('@').Append(mailbox.Domain).Append(">\r\n");
        if (messageId.Length > 0)
        {
            sb.Append("In-Reply-To: ").Append(messageId).Append("\r\n");
            sb.Append("References: ").Append(messageId).Append("\r\n");
        }
        sb.Append("Auto-Submitted: auto-replied\r\n");
        sb.Append("X-Auto-Response-Suppress: All\r\n");
        sb.Append("Precedence: auto_reply\r\n");
        sb.Append("MIME-Version: 1.0\r\n");
        sb.Append("Content-Type: text/plain; charset=utf-8\r\n");
        sb.Append("Content-Transfer-Encoding: 8bit\r\n\r\n");
        sb.Append(away.Text.Replace("\r\n", "\n", System.StringComparison.Ordinal).Replace("\n", "\r\n", System.StringComparison.Ordinal)).Append("\r\n");
        byte[] reply = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        // RFC 3834: an automatic reply goes with an empty envelope sender, so it can never bounce back and forth.
        await this.SendOnAsync(string.Empty, sender, reply, ct, mailbox.Address).ConfigureAwait(false);
        away.Replied[sender] = now;
        foreach (string old in away.Replied.Where(kv => kv.Value < now.AddDays(-90)).Select(kv => kv.Key).ToList())
        {
            away.Replied.Remove(old);
        }
        await AwayReply.WriteAsync(this.store, mailbox.Id, away, ct).ConfigureAwait(false);
        this.log?.Invoke($"Mailbox: {mailbox.Address} sent its away reply to {sender}");
        Anjal.Smtp.Counters.Increment("anjal_mailbox_away_replies_total");
    }

    /// <summary>
    /// rc.22 (item 39): the operator hears at once when mail is held because the scanner is down.
    /// The webmail repeats the notice every 30 minutes while anything is still held.
    /// </summary>
    private async System.Threading.Tasks.Task HeldNoticeAsync(System.Collections.Generic.List<string> heldFor, Anjal.Smtp.DeliveryContext ctx, FileJudgement files, System.Threading.CancellationToken ct)
    {
        this.log?.Invoke($"Mailbox: scanner unavailable ({files.Detail}); mail from {ctx.EnvelopeFrom} held for {string.Join(", ", heldFor)}");
        Anjal.Smtp.Counters.Increment("anjal_virus_held_total");
        string? ops = this.OperatorPostmaster ?? (this.ServerHostName is string host ? "postmaster@" + ParentDomain(host) : null);
        if (ops is null || !TrySplitAddress(ops, out string ol, out string od))
        {
            return;
        }
        string text = "The virus scanner (ClamAV) did not answer: " + files.Detail + "\r\n\r\n"
            + "A message with files from " + ctx.EnvelopeFrom + " is held in quarantine for " + string.Join(", ", heldFor) + ".\r\n"
            + "Files: " + string.Join(", ", files.Files) + "\r\n\r\n"
            + "Check clamd (systemctl status clamav-daemon). Held mail is scanned again every five minutes and delivered once the scanner answers; the Anjal console (Virus scanning) lists what is held.\r\n";
        var sb = new System.Text.StringBuilder();
        sb.Append("From: \"Anjal\" <no-reply@").Append(od).Append(">\r\n");
        sb.Append("To: <").Append(ol).Append('@').Append(od).Append(">\r\n");
        sb.Append("Subject: Mail held: the virus scanner is not answering\r\n");
        sb.Append("Date: ").Append(System.DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("Message-ID: <").Append(System.Guid.NewGuid().ToString("N")).Append('@').Append(od).Append(">\r\n");
        sb.Append("Auto-Submitted: auto-generated\r\n");
        sb.Append("MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\n");
        sb.Append(text);
        await this.SendOnAsync(string.Empty, ol + "@" + od, System.Text.Encoding.UTF8.GetBytes(sb.ToString()), ct, "no-reply@" + od, security: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Deliver a message Anjal itself makes (a forwarded copy, an away reply, a notice): here when
    /// the recipient is a mailbox on this server, otherwise through the outgoing queue.
    /// </summary>
    private async System.Threading.Tasks.Task SendOnAsync(string envelopeFrom, string to, byte[] bytes, System.Threading.CancellationToken ct, string? asUser = null, bool security = false)
    {
        if (await this.ResolveAsync(to, ct).ConfigureAwait(false) is not null)
        {
            await this.DeliverAsync(new Anjal.Smtp.DeliveryContext
            {
                EnvelopeFrom = envelopeFrom,
                EnvelopeTo = new[] { to },
                RawBytes = bytes,
                AuthenticatedUser = asUser ?? (envelopeFrom.Length > 0 ? envelopeFrom : "anjal"),
                AnjalSecurityMail = security,
            }, ct).ConfigureAwait(false);
            return;
        }
        if (this.store is Anjal.Store.IMessageStore queue)
        {
            System.DateTimeOffset now = System.DateTimeOffset.UtcNow;
            await queue.EnqueueOutboundAsync(new Anjal.Store.OutboundMessage
            {
                EnvelopeFrom = envelopeFrom,
                EnvelopeTo = to,
                RawBytes = bytes,
                CreatedAt = now,
                NextAttemptAt = now,
                GiveUpAt = now + Anjal.Store.OutboundMessage.DefaultGiveUp,
            }, ct).ConfigureAwait(false);
        }
        else
        {
            this.log?.Invoke($"Mailbox: no outgoing queue; message to {to} not sent");
        }
    }

    private async System.Threading.Tasks.Task<bool> IsContactAsync(System.Guid mailboxId, string address, System.Threading.CancellationToken ct)
    {
        string? json = await this.store.GetMailboxDocumentAsync(mailboxId, "contacts", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return false;
            }
            foreach (System.Text.Json.JsonElement c in doc.RootElement.EnumerateArray())
            {
                if (c.TryGetProperty("Address", out System.Text.Json.JsonElement a) && a.ValueKind == System.Text.Json.JsonValueKind.String
                    && string.Equals(a.GetString(), address, System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
        return false;
    }

    private static string FromLine(Anjal.Store.MailboxRow mailbox)
    {
        string name = mailbox.DisplayName.Trim();
        if (name.Length == 0)
        {
            return "<" + mailbox.Address + ">";
        }
        return EncodeHeader("\"" + name.Replace("\\", "\\\\", System.StringComparison.Ordinal).Replace("\"", "\\\"", System.StringComparison.Ordinal) + "\"") + " <" + mailbox.Address + ">";
    }

    /// <summary>A header value as RFC 2047 text when it is not plain ASCII.</summary>
    /// <param name="text">The value.</param>
    /// <returns>The value, or an encoded word.</returns>
    public static string EncodeHeader(string text)
    {
        System.ArgumentNullException.ThrowIfNull(text);
        foreach (char c in text)
        {
            if (c > 126 || c < 32)
            {
                return "=?utf-8?B?" + System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)) + "?=";
            }
        }
        return text;
    }

    private static byte[] Prepend(byte[] head, byte[] body)
    {
        var result = new byte[head.Length + body.Length];
        head.CopyTo(result, 0);
        body.CopyTo(result, head.Length);
        return result;
    }

    private static byte[] StripReturnPath(byte[] raw)
    {
        byte[] withLine = WithReturnPath(raw, string.Empty);
        int nl = System.Array.IndexOf(withLine, (byte)'\n');
        return nl < 0 ? raw : withLine[(nl + 1)..];
    }

    private static string Clip(string text, int max)
    {
        string t = (text ?? string.Empty).Trim();
        return t.Length <= max ? t : t[..max];
    }

    /// <summary>
    /// File a copy of a message a mailbox itself sent into its Sent folder,
    /// already marked read - what the webmail does after sending, for mail
    /// that arrives through SMTP submission instead. Returns false when the
    /// address is not a local mailbox (a service account, for example).
    /// </summary>
    /// <param name="address">The sending mailbox's address.</param>
    /// <param name="rawBytes">The message as submitted.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when a copy was filed.</returns>
    public async System.Threading.Tasks.Task<bool> FileSentCopyAsync(string address, byte[] rawBytes, System.Threading.CancellationToken ct = default) =>
        await this.FileSentCopyWithEvidenceAsync(address, rawBytes, evidenceId: null, ct).ConfigureAwait(false) is not null;

    /// <summary>
    /// <see cref="FileSentCopyAsync"/>, also linking the copy to the evidence of
    /// the submission as received (v1.0.0-rc.8), and returning the copy's id.
    /// </summary>
    /// <param name="address">The sending mailbox's address.</param>
    /// <param name="rawBytes">The message as submitted.</param>
    /// <param name="evidenceId">The evidence of the submission, or null.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The Sent copy's id, or null when the address is not a local mailbox.</returns>
    public async System.Threading.Tasks.Task<System.Guid?> FileSentCopyWithEvidenceAsync(string address, byte[] rawBytes, System.Guid? evidenceId, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(address);
        System.ArgumentNullException.ThrowIfNull(rawBytes);
        (Anjal.Store.TenantRow Tenant, Anjal.Store.MailboxRow Mailbox)? resolved = await this.ResolveAsync(address, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            return null;
        }
        (Anjal.Store.TenantRow tenant, Anjal.Store.MailboxRow mailbox) = resolved.Value;

        Anjal.Mime.MimeMessage? parsed;
        try
        {
            parsed = Anjal.Mime.MimeParser.Parse(rawBytes);
        }
#pragma warning disable CA1031 // An unparseable copy is still filed; the raw bytes are the truth.
        catch (System.Exception)
        {
            parsed = null;
        }
#pragma warning restore CA1031

        Anjal.Store.FolderRow sent = await this.store.EnsureFolderAsync(mailbox.Id, "Sent", ct).ConfigureAwait(false);
        MaildirWriteResult written = await this.maildir.WriteAsync(tenant.Slug, mailbox.Address, sent.Name, rawBytes, ct).ConfigureAwait(false);
        string? seenPath = this.maildir.SetFlags(tenant.Slug, mailbox.Address, sent.Name, written.RelativePath, seen: true, flagged: false, answered: false);
        Anjal.Store.MessageRow saved = await this.store.SaveMessageAsync(new Anjal.Store.MessageRow
        {
            MailboxId = mailbox.Id,
            FolderId = sent.Id,
            EvidenceId = evidenceId,
            MaildirFile = seenPath ?? written.RelativePath,
            EnvelopeFrom = mailbox.Address,
            MessageId = parsed?.MessageId ?? string.Empty,
            FromHeader = parsed?.Headers.Get("From") ?? mailbox.Address,
            ToHeader = parsed?.Headers.Get("To") ?? string.Empty,
            Subject = parsed?.Subject ?? string.Empty,
            DateHeader = parsed?.Date ?? string.Empty,
            SizeBytes = written.SizeBytes,
            Seen = true,
            SpamChecked = false,
            HasAttachments = HasAttachment(parsed?.Body),
            BodyText = MessageText.Extract(parsed),
        }, ct).ConfigureAwait(false);
        await this.store.AddMailboxUsageAsync(mailbox.Id, written.SizeBytes, ct).ConfigureAwait(false);
        if (evidenceId is System.Guid e && this.store is Anjal.Store.IEvidenceStore evidenceStore)
        {
            await evidenceStore.RaiseEvidenceRetentionAsync(e, tenant.EvidenceRetentionDays, ct).ConfigureAwait(false);
        }
        this.log?.Invoke($"Filed sent copy for {mailbox.Address} ({written.SizeBytes} bytes)");
        return saved.Id;
    }

    /// <summary>
    /// Whether a parsed message carries an attachment: any part with a
    /// filename, or any non-text part inside a multipart body. A plain
    /// text or HTML message on its own does not count.
    /// </summary>
    /// <param name="entity">Root entity, or null when parsing failed.</param>
    public static bool HasAttachment(Anjal.Mime.MimeEntity? entity)
    {
        if (entity is Anjal.Mime.MimePart part)
        {
            // The same test the message view uses when it lists attachments,
            // so the flag and the list can never disagree.
            string disposition = part.Headers.Get("Content-Disposition") ?? string.Empty;
            if (disposition.StartsWith("attachment", System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            string type = part.ContentType.MimeType;
            return !type.StartsWith("text/", System.StringComparison.OrdinalIgnoreCase);
        }
        if (entity is Anjal.Mime.MimeMultipart multi)
        {
            foreach (Anjal.Mime.MimeEntity child in multi.Parts)
            {
                if (HasAttachment(child))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// The category a mailbox's rules put this sender in, or null. An exact
    /// address beats a domain rule, the same precedence sender rules use.
    /// </summary>
    /// <summary>
    /// Whether a rule may file mail in a folder: never Drafts, Sent, Scheduled
    /// or Junk (Junk is Anjal's own decision), and never an empty name.
    /// </summary>
    /// <param name="folder">The folder a rule names.</param>
    public static bool IsRuleTarget(string folder)
    {
        string f = (folder ?? string.Empty).Trim();
        return f.Length > 0 && f.Length <= 100
            && !string.Equals(f, "Drafts", System.StringComparison.OrdinalIgnoreCase)
            && !string.Equals(f, "Sent", System.StringComparison.OrdinalIgnoreCase)
            && !string.Equals(f, "Scheduled", System.StringComparison.OrdinalIgnoreCase)
            && !string.Equals(f, JunkFolder, System.StringComparison.OrdinalIgnoreCase)
            && !string.Equals(f, Anjal.Store.FolderRow.Inbox, System.StringComparison.OrdinalIgnoreCase);
    }

    private async System.Threading.Tasks.Task<RuleOutcome?> RuleForAsync(Anjal.Store.TenantRow tenant, Anjal.Store.MailboxRow mailbox, Anjal.Mime.MimeMessage parsed, string fromAddress, System.Threading.CancellationToken ct)
    {
        IReadOnlyList<MailRule> rules = MailRules.Parse(await this.store.GetMailboxDocumentAsync(mailbox.Id, MailRules.Kind, ct).ConfigureAwait(false));
        if (rules.Count == 0)
        {
            return null;
        }
        IReadOnlyDictionary<string, IReadOnlyList<string>> groups = rules.Any(r => r.Conditions.Any(c => c.Field == "group"))
            ? MailRules.ParseGroups(await this.store.GetMailboxDocumentAsync(mailbox.Id, MailRules.GroupsKind, ct).ConfigureAwait(false))
            : new System.Collections.Generic.Dictionary<string, IReadOnlyList<string>>();
        int at = fromAddress.LastIndexOf('@');
        string fromDomain = at < 0 ? string.Empty : fromAddress[(at + 1)..];
        bool outside = true;
        foreach (Anjal.Store.TenantDomainRow d in await this.store.ListTenantDomainsAsync(tenant.Id, ct).ConfigureAwait(false))
        {
            if (string.Equals(d.Domain, fromDomain, System.StringComparison.OrdinalIgnoreCase))
            {
                outside = false;
            }
        }
        var subject = new RuleSubject
        {
            From = Anjal.Mime.EncodedWordDecoder.Decode(parsed.Headers.Get("From") ?? string.Empty),
            FromAddress = fromAddress,
            To = Anjal.Mime.EncodedWordDecoder.Decode((parsed.Headers.Get("To") ?? string.Empty) + ", " + (parsed.Headers.Get("Cc") ?? string.Empty)),
            Subject = Anjal.Mime.EncodedWordDecoder.Decode(parsed.Subject ?? string.Empty),
            Body = MessageText.Extract(parsed),
            HasAttachment = HasAttachment(parsed.Body),
            HasUnsubscribe = parsed.Headers.Get("List-Unsubscribe") is not null,
            FromOutside = outside,
        };
        return MailRules.Evaluate(rules, subject, groups);
    }

    private async System.Threading.Tasks.Task<System.Guid?> CategoryForAsync(System.Guid mailboxId, string envelopeFrom, string fromHeader, System.Threading.CancellationToken ct)
    {
        System.Collections.Generic.IReadOnlyList<Anjal.Store.CategoryRuleRow> rules =
            await this.store.ListCategoryRulesAsync(mailboxId, ct).ConfigureAwait(false);
        if (rules.Count == 0)
        {
            return null;
        }

        var addresses = new System.Collections.Generic.List<string>();
        if (envelopeFrom.Length > 0)
        {
            addresses.Add(envelopeFrom);
        }
        foreach (Anjal.Mime.MailAddress a in Anjal.Mime.AddressParser.Parse(Anjal.Mime.EncodedWordDecoder.Decode(fromHeader)))
        {
            addresses.Add(a.Address);
        }

        System.Guid? domainMatch = null;
        foreach (Anjal.Store.CategoryRuleRow rule in rules)
        {
            foreach (string address in addresses)
            {
                if (!TrySplitAddress(address, out string local, out string domain))
                {
                    continue;
                }
                string full = local + "@" + domain;
                if (string.Equals(rule.Pattern, full, System.StringComparison.OrdinalIgnoreCase))
                {
                    return rule.CategoryId;
                }
                if (rule.Pattern.StartsWith('@') &&
                    (string.Equals(rule.Pattern.AsSpan(1).ToString(), domain, System.StringComparison.OrdinalIgnoreCase) ||
                     domain.EndsWith("." + rule.Pattern.AsSpan(1).ToString(), System.StringComparison.OrdinalIgnoreCase)))
                {
                    domainMatch ??= rule.CategoryId;
                }
            }
        }
        return domainMatch;
    }

    /// <summary>Whether the mailbox trusts the sender although their mail arrives unencrypted.</summary>
    private async System.Threading.Tasks.Task<bool> IsTrustedAsync(System.Guid mailboxId, string fromHeaderAddress, string envelopeFrom, System.Threading.CancellationToken ct)
    {
        IReadOnlyList<string> trusted = await this.store.ListTrustedSendersAsync(mailboxId, ct).ConfigureAwait(false);
        return (fromHeaderAddress.Length > 0 && trusted.Contains(fromHeaderAddress.ToLowerInvariant()))
            || (envelopeFrom.Length > 0 && trusted.Contains(envelopeFrom.ToLowerInvariant()));
    }
}
