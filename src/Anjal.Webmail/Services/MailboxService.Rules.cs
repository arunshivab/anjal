using Anjal.Mailbox;
using Anjal.Mime;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>How a rule would have done over recent mail (the board: "Tried on the last 30 days").</summary>
/// <param name="Matches">The messages it would have filed.</param>
/// <param name="BySender">The commonest senders among them, with counts.</param>
public sealed record RuleTrial(int Matches, IReadOnlyList<(string Sender, int Count)> BySender);

/// <summary>Folders and rules (rc.12, items 24 and 31; the board SetRules).</summary>
public sealed partial class MailboxService
{
    /// <summary>The kind of the document holding a rule being tried, unsaved (D-176, rc.23).</summary>
    public const string RuleDraftKind = "ruledraft";

    /// <summary>The standard folders, which cannot be created, removed or used as a person's own.</summary>
    public static readonly IReadOnlySet<string> StandardFolders = new HashSet<string>(
        new[] { FolderRow.Inbox, ScheduledFolder, "Outbox", SnoozedFolder, AttachmentPolicy.QuarantineFolder, "Drafts", "Sent", "Archive", MailboxSink.JunkFolder, "Trash" },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>The person's rules, in the order they run.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyList<MailRule>> ListRulesAsync(Guid mailboxId, CancellationToken ct = default) =>
        MailRules.Parse(await this.store.GetMailboxDocumentAsync(mailboxId, MailRules.Kind, ct).ConfigureAwait(false));

    /// <summary>
    /// Keep a rule being edited, unsaved, so that "Try on the last 30 days" can show what it would
    /// match without saving it (D-176, DEF-137, rc.23: Try saved the rule, so a forwarding rule
    /// started forwarding new mail the moment it was tried).
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="rule">The rule as typed.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public Task KeepRuleDraftAsync(Guid mailboxId, MailRule rule, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return this.WriteDocumentAsync(mailboxId, RuleDraftKind, rule, ct);
    }

    /// <summary>The rule being tried, unsaved (D-176), or null.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rule as typed, or null.</returns>
    public Task<MailRule?> RuleDraftAsync(Guid mailboxId, CancellationToken ct = default) =>
        this.ReadDocumentAsync<MailRule>(mailboxId, RuleDraftKind, ct);

    /// <summary>Forget the rule being tried (it was saved, or another was chosen).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public Task ForgetRuleDraftAsync(Guid mailboxId, CancellationToken ct = default) =>
        this.store.SetMailboxDocumentAsync(mailboxId, RuleDraftKind, "null", ct);

    /// <summary>Add or change a rule. Returns a message for the person, or null on success.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="rule">The rule; an unknown id adds it at the end.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> SaveRuleAsync(Guid mailboxId, MailRule rule, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        rule.Name = Clip(rule.Name, 100);
        if (rule.Name.Length == 0)
        {
            return "Give the rule a name.";
        }
        var conditions = rule.Conditions
            .Where(c => c.Field is "attachment" or "unsubscribe" or "outside" || c.Value.Trim().Length > 0)
            .Select(c => new RuleCondition { Field = c.Field, Op = c.Op, Value = Clip(c.Value, 200) })
            .Take(5)
            .ToList();
        if (conditions.Count == 0)
        {
            return "Add at least one condition: what the mail must have for the rule to act.";
        }
        string move = rule.MoveTo.Trim();
        if (move.Length > 0 && !MailboxSink.IsRuleTarget(move) && !string.Equals(move, "Archive", StringComparison.Ordinal) && !string.Equals(move, "Trash", StringComparison.Ordinal))
        {
            return "Choose Archive, Trash or one of your own folders.";
        }
        // rc.22 (item 29, owner 18 Oct 2026): forwarding is only by rule; individuals may forward
        // anywhere; an organisation's people outside it only when its administrator allows; a copy
        // always stays.
        string forward = rule.ForwardTo.Trim().ToLowerInvariant();
        if (forward.Length > 0)
        {
            if (await this.ForwardProblemAsync(mailboxId, forward, ct).ConfigureAwait(false) is string bad)
            {
                return bad;
            }
        }
        if (move.Length == 0 && !rule.MarkRead && !rule.Flag && forward.Length == 0)
        {
            return "Choose what the rule does: move, mark read, flag, or forward a copy.";
        }
        if (move.Length > 0)
        {
            await this.store.EnsureFolderAsync(mailboxId, move, ct).ConfigureAwait(false);
        }
        var clean = new MailRule
        {
            Id = rule.Id,
            Name = rule.Name,
            Enabled = rule.Enabled,
            Conditions = conditions,
            MoveTo = move,
            MarkRead = rule.MarkRead,
            Flag = rule.Flag,
            ForwardTo = forward,
        };
        List<MailRule> all = (await this.ListRulesAsync(mailboxId, ct).ConfigureAwait(false)).ToList();
        int index = all.FindIndex(r => r.Id == clean.Id);
        if (index >= 0)
        {
            all[index] = clean;
        }
        else
        {
            if (all.Count >= MailRules.MaxRules)
            {
                return "You have the most rules allowed. Remove one first.";
            }
            all.Add(clean);
        }
        await this.store.SetMailboxDocumentAsync(mailboxId, MailRules.Kind, MailRules.Write(all), ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Remove a rule.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ruleId">The rule.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<bool> DeleteRuleAsync(Guid mailboxId, Guid ruleId, CancellationToken ct = default)
    {
        List<MailRule> all = (await this.ListRulesAsync(mailboxId, ct).ConfigureAwait(false)).ToList();
        int removed = all.RemoveAll(r => r.Id == ruleId);
        if (removed > 0)
        {
            await this.store.SetMailboxDocumentAsync(mailboxId, MailRules.Kind, MailRules.Write(all), ct).ConfigureAwait(false);
        }
        return removed > 0;
    }

    /// <summary>Move a rule up or down one place (rules run top first).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ruleId">The rule.</param>
    /// <param name="up">True for up.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task MoveRuleAsync(Guid mailboxId, Guid ruleId, bool up, CancellationToken ct = default)
    {
        List<MailRule> all = (await this.ListRulesAsync(mailboxId, ct).ConfigureAwait(false)).ToList();
        int i = all.FindIndex(r => r.Id == ruleId);
        int j = up ? i - 1 : i + 1;
        if (i < 0 || j < 0 || j >= all.Count)
        {
            return;
        }
        (all[i], all[j]) = (all[j], all[i]);
        await this.store.SetMailboxDocumentAsync(mailboxId, MailRules.Kind, MailRules.Write(all), ct).ConfigureAwait(false);
    }

    /// <summary>Switch a rule on or off.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ruleId">The rule.</param>
    /// <param name="on">On or off.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task SetRuleEnabledAsync(Guid mailboxId, Guid ruleId, bool on, CancellationToken ct = default)
    {
        List<MailRule> all = (await this.ListRulesAsync(mailboxId, ct).ConfigureAwait(false)).ToList();
        MailRule? rule = all.FirstOrDefault(r => r.Id == ruleId);
        if (rule is null)
        {
            return;
        }
        rule.Enabled = on;
        await this.store.SetMailboxDocumentAsync(mailboxId, MailRules.Kind, MailRules.Write(all), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Try a rule on the Inbox's last 30 days, or apply it to them. Only mail
    /// still in the Inbox is considered, so a rule never pulls mail back out
    /// of a folder the person filed it in. Returns what it found.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="rule">The rule.</param>
    /// <param name="apply">Move them now, as the rule says.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<RuleTrial> TryRuleAsync(Guid mailboxId, MailRule rule, bool apply, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        FolderRow? inbox = await this.GetFolderAsync(mailboxId, FolderRow.Inbox, ct).ConfigureAwait(false);
        if (inbox is null)
        {
            return new RuleTrial(0, Array.Empty<(string, int)>());
        }
        IReadOnlyList<string> domains = await this.OrganisationDomainsAsync(mailboxId, ct).ConfigureAwait(false);
        IReadOnlyDictionary<string, IReadOnlyList<string>> groups = MailRules.ParseGroups(await this.store.GetMailboxDocumentAsync(mailboxId, MailRules.GroupsKind, ct).ConfigureAwait(false));
        DateTimeOffset since = DateTimeOffset.UtcNow.AddDays(-30);
        var matched = new List<MessageRow>();
        for (int offset = 0; offset < 5000; offset += 500)
        {
            IReadOnlyList<MessageRow> page = await this.store.ListMessagesAsync(mailboxId, inbox.Id, 500, offset, ct).ConfigureAwait(false);
            foreach (MessageRow m in page.Where(m => m.ReceivedAt >= since))
            {
                string fromAddress = Anjal.Spam.SpamScorer.FirstAddress(m.FromHeader);
                int at = fromAddress.LastIndexOf('@');
                var subject = new RuleSubject
                {
                    From = EncodedWordDecoder.Decode(m.FromHeader),
                    FromAddress = fromAddress,
                    To = EncodedWordDecoder.Decode(m.ToHeader),
                    Subject = EncodedWordDecoder.Decode(m.Subject),
                    Body = m.BodyText ?? string.Empty,
                    HasAttachment = m.HasAttachments,
                    HasUnsubscribe = false,
                    FromOutside = at < 0 || !domains.Contains(fromAddress[(at + 1)..], StringComparer.OrdinalIgnoreCase),
                };
                var single = new MailRule { Name = rule.Name, Enabled = true, Conditions = rule.Conditions, MoveTo = rule.MoveTo, MarkRead = rule.MarkRead, Flag = rule.Flag, ForwardTo = rule.ForwardTo };
                if (MailRules.Evaluate(new[] { single }, subject, groups) is not null)
                {
                    matched.Add(m);
                }
            }
            if (page.Count < 500 || page.Any(m => m.ReceivedAt < since))
            {
                break;
            }
        }
        if (apply)
        {
            // rc.22 (item 29): a rule can also be applied to older mail - forwarded copies too, at most 200 at a time.
            string forward = rule.ForwardTo.Trim().ToLowerInvariant();
            int forwarded = 0;
            foreach (MessageRow m in matched)
            {
                if (rule.MarkRead || rule.Flag)
                {
                    await this.SetFlagsAsync(mailboxId, m.Id, rule.MarkRead || m.Seen, rule.Flag || m.Flagged, m.Answered, ct).ConfigureAwait(false);
                }
                if (forward.Length > 0 && forwarded < 200 && await this.ForwardProblemAsync(mailboxId, forward, ct).ConfigureAwait(false) is null
                    && await this.ForwardOlderAsync(mailboxId, m.Id, forward, ct).ConfigureAwait(false))
                {
                    forwarded++;
                }
                if (rule.MoveTo.Trim().Length > 0)
                {
                    await this.MoveAsync(mailboxId, m.Id, rule.MoveTo.Trim(), ct).ConfigureAwait(false);
                }
            }
        }
        var bySender = matched
            .GroupBy(m => Anjal.Spam.SpamScorer.FirstAddress(m.FromHeader), StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(g => g.Item2)
            .Take(3)
            .ToList();
        return new RuleTrial(matched.Count, bySender);
    }

    /// <summary>
    /// What is wrong with a forwarding address for this person (rc.22, item 29): not an address, the
    /// mailbox itself, or outside the organisation when its administrator has not allowed that. Null when fine.
    /// </summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="address">The address, lower case.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The problem, or null.</returns>
    public async Task<string?> ForwardProblemAsync(Guid mailboxId, string address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!MailboxSink.TrySplitAddress(address, out string local, out string domain) || !Anjal.Spam.SenderRules.IsValidPattern(local + "@" + domain))
        {
            return "Give the address a copy is forwarded to, such as name@example.com.";
        }
        MailboxRow? me = await this.store.GetMailboxByIdAsync(mailboxId, ct).ConfigureAwait(false);
        if (me is null)
        {
            return "That mailbox was not found.";
        }
        if (string.Equals(local + "@" + domain, me.Address, StringComparison.OrdinalIgnoreCase))
        {
            return "That is this mailbox itself.";
        }
        IReadOnlyList<string> domains = await this.OrganisationDomainsAsync(mailboxId, ct).ConfigureAwait(false);
        bool outside = !domains.Contains(domain, StringComparer.OrdinalIgnoreCase);
        if (outside && !await this.IsIndividualsTenantAsync(me.TenantId, ct).ConfigureAwait(false)
            && !(await this.MailPolicyOfAsync(me.TenantId, ct).ConfigureAwait(false)).ForwardingOutside)
        {
            return $"{local}@{domain} is outside your organisation, and your administrator has not allowed forwarding outside it. Forwarding inside is always allowed.";
        }
        return null;
    }

    /// <summary>Whether an address is outside the person's organisation (for the forwarding notice).</summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="address">The address.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when outside.</returns>
    public async Task<bool> IsOutsideAsync(Guid mailboxId, string address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        int at = address.LastIndexOf('@');
        IReadOnlyList<string> domains = await this.OrganisationDomainsAsync(mailboxId, ct).ConfigureAwait(false);
        return at < 0 || !domains.Contains(address[(at + 1)..], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Create one of the person's own folders. Returns a message, or null on success.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="name">The folder's name.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> CreateFolderAsync(Guid mailboxId, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        string n = name.Trim();
        if (n.Length == 0 || n.Length > 100 || !FolderRow.IsValidName(n))
        {
            return "Folder names are 1 to 100 characters, without / or \\.";
        }
        if (StandardFolders.Contains(n))
        {
            return "That name belongs to a standard folder.";
        }
        if ((await this.store.ListFoldersAsync(mailboxId, ct).ConfigureAwait(false)).Any(f => string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)))
        {
            return "There is already a folder with this name.";
        }
        await this.store.EnsureFolderAsync(mailboxId, n, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Remove one of the person's own folders, only when it is empty. Returns a message, or null.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="name">The folder's name.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> DeleteFolderAsync(Guid mailboxId, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (StandardFolders.Contains(name.Trim()))
        {
            return "Standard folders cannot be removed.";
        }
        FolderRow? folder = await this.GetFolderAsync(mailboxId, name.Trim(), ct).ConfigureAwait(false);
        if (folder is null)
        {
            return null;
        }
        if ((await this.ListRulesAsync(mailboxId, ct).ConfigureAwait(false)).Any(r => string.Equals(r.MoveTo, folder.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return "A rule files mail into this folder. Change or remove that rule first.";
        }
        return await this.store.DeleteEmptyFolderAsync(mailboxId, folder.Id, ct).ConfigureAwait(false)
            ? null
            : "Only an empty folder can be removed. Move its messages first.";
    }

    /// <summary>
    /// Item 31: when a DMARC report arrives and there is no rule for them yet,
    /// the ready-made rule the board offers: From contains "dmarc" or the
    /// subject starts "Report domain:", moved to "DMARC reports" and marked read.
    /// </summary>
    public static MailRule DmarcRule() => new()
    {
        Name = "DMARC reports",
        Conditions = new List<RuleCondition> { new() { Field = "subject", Op = "startswith", Value = "Report domain:" } },
        MoveTo = "DMARC reports",
        MarkRead = true,
    };

    /// <summary>Forward a copy of a message already in the mailbox (rc.22, item 29), through the same path delivery uses.</summary>
    private async Task<bool> ForwardOlderAsync(Guid mailboxId, Guid messageId, string target, CancellationToken ct)
    {
        (MessageRow Row, FolderRow Folder, byte[] Raw)? found = await this.ReadRawAsync(mailboxId, messageId, ct).ConfigureAwait(false);
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        if (found is null || context is null)
        {
            return false;
        }
        MimeMessage parsed;
        try
        {
            parsed = MimeParser.Parse(found.Value.Raw);
        }
        catch (MimeParseException)
        {
            return false;
        }
        if (MailboxSink.IsAutomaticMail(parsed.Headers) || parsed.Headers.GetAll(MailboxSink.ForwardedHeader).Count >= MailboxSink.MaxForwardHops)
        {
            return false;
        }
        MailboxRow mailbox = context.Value.Mailbox;
        string now = DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", System.Globalization.CultureInfo.InvariantCulture);
        string head = "Resent-From: <" + mailbox.Address + ">\r\nResent-To: <" + target + ">\r\nResent-Date: " + now + "\r\n"
            + "Resent-Message-ID: <" + Guid.NewGuid().ToString("N") + "@" + mailbox.Domain + ">\r\n" + MailboxSink.ForwardedHeader + ": " + mailbox.Address + "\r\n";
        byte[] headBytes = System.Text.Encoding.ASCII.GetBytes(head);
        byte[] body = MailboxSink.WithReturnPath(found.Value.Raw, string.Empty);
        int nl = Array.IndexOf(body, (byte)'\n');
        byte[] rest = nl < 0 ? body : body[(nl + 1)..];
        var copy = new byte[headBytes.Length + rest.Length];
        headBytes.CopyTo(copy, 0);
        rest.CopyTo(copy, headBytes.Length);
        var sink = new MailboxSink(this.store, this.maildir);
        if (await sink.ResolveAsync(target, ct).ConfigureAwait(false) is not null)
        {
            await sink.DeliverAsync(new Anjal.Smtp.DeliveryContext { EnvelopeFrom = mailbox.Address, EnvelopeTo = new[] { target }, RawBytes = copy, AuthenticatedUser = mailbox.Address }, ct).ConfigureAwait(false);
        }
        else
        {
            DateTimeOffset at = DateTimeOffset.UtcNow;
            await this.messageStore.EnqueueOutboundAsync(new OutboundMessage { EnvelopeFrom = mailbox.Address, EnvelopeTo = target, RawBytes = copy, CreatedAt = at, NextAttemptAt = at, GiveUpAt = at + OutboundMessage.DefaultGiveUp }, ct).ConfigureAwait(false);
        }
        await this.messageStore.AppendAuditAsync(new AuditEvent { Actor = mailbox.Address, Action = MailboxSink.ForwardAudit, Subject = target, Detail = Clip(parsed.Subject, 120) }, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>rc.26 (item 80): the rules' match counts document (see <see cref="MailboxSink.RuleMatches"/>).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The document, or null.</returns>
    public Task<string?> RuleCountsAsync(Guid mailboxId, CancellationToken ct = default) =>
        this.store.GetMailboxDocumentAsync(mailboxId, MailboxSink.RuleCountsKind, ct);
}
