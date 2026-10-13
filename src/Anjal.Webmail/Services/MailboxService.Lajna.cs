using System.Text.RegularExpressions;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>A message Lajna found (design review [8g]).</summary>
/// <param name="Id">The message.</param>
/// <param name="From">Who sent it.</param>
/// <param name="Subject">Its subject.</param>
/// <param name="Preview">The start of its text.</param>
/// <param name="At">When it arrived.</param>
/// <param name="Unread">True while unread.</param>
/// <param name="Files">True when it has attachments.</param>
public sealed record LajnaMail(Guid Id, string From, string Subject, string Preview, DateTimeOffset At, bool Unread, bool Files);

/// <summary>Who covered for the person when they were last away, as their own mail shows it.</summary>
/// <param name="Name">The colleague's name (or address).</param>
/// <param name="At">When the mail that says so was sent.</param>
/// <param name="Subject">That mail's subject.</param>
/// <param name="Copied">True when it is only that the colleague was copied on the leave mail.</param>
public sealed record LajnaCover(string Name, DateTimeOffset At, string Subject, bool Copied);

/// <summary>What happened to a message the person sent (design review [8d]).</summary>
/// <param name="Status">Delivered, Delayed, Queued, Bounced, Failed or Suppressed; "Inside" when it went to someone in the organisation.</param>
/// <param name="To">The recipient.</param>
/// <param name="Subject">Its subject.</param>
/// <param name="At">When it was sent.</param>
/// <param name="Tries">How many times sending was tried.</param>
/// <param name="LastReply">The receiving server's last reply.</param>
/// <param name="Next">The next try, while it is being tried.</param>
/// <param name="GiveUp">When Anjal gives up.</param>
/// <param name="Attempts">Each attempt.</param>
/// <param name="App">The application that sent it, for an administrator's question; null for the person's own.</param>
public sealed record LajnaStory(string Status, string To, string Subject, DateTimeOffset At, int Tries, string LastReply, DateTimeOffset? Next, DateTimeOffset GiveUp, IReadOnlyList<SendingAttempt> Attempts, string? App);

/// <summary>
/// rc.18 (design review section 11, owner 11-12 Oct 2026): what Lajna reads, inside Anjal and
/// only for the person asking - their own mail, their own sent messages and, for an administrator,
/// their organisation's applications. Nothing leaves the server and nothing asked is kept.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>
    /// The people a name may mean, best first: the person's contacts and colleagues whose name or
    /// address contains it, the ones written to most first (design review [8g]).
    /// </summary>
    /// <param name="personId">The person asking.</param>
    /// <param name="who">A name, part of one, or an address.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>(address, name) pairs, at most five.</returns>
    public async Task<IReadOnlyList<(string Address, string Name)>> WhoIsAsync(Guid personId, string who, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(who);
        string w = who.Trim().Trim('.', ',', '?', '!', '"').ToLowerInvariant();
        if (w.Length == 0)
        {
            return Array.Empty<(string, string)>();
        }
        if (w.Contains('@', StringComparison.Ordinal))
        {
            return new[] { (w, w) };
        }
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Contact c in await this.ListContactsAsync(personId, ct).ConfigureAwait(false))
        {
            if (Matches(c.DisplayName, c.Address, w))
            {
                found.TryAdd(c.Address, c.DisplayName.Length > 0 ? c.DisplayName : c.Address);
            }
        }
        foreach ((string address, string name) in await this.ColleaguesAsync(personId, ct).ConfigureAwait(false))
        {
            if (Matches(name, address, w))
            {
                found.TryAdd(address, name.Length > 0 ? name : address);
            }
        }
        // rc.18 screen check (12 Oct 2026): someone who writes to the person but was never saved as a contact
        // ("the last 5 mails from Meera") is found among the senders of the person's own mail.
        if (found.Count == 0)
        {
            (IReadOnlyList<MessageRow> fromThem, _) = await this.SearchFilteredAsync(personId, null, string.Empty, w, null, null, false, 0, 50, ct: ct).ConfigureAwait(false);
            foreach (MessageRow m in fromThem)
            {
                IReadOnlyList<Anjal.Mime.MailAddress> from = Anjal.Mime.AddressParser.Parse(Anjal.Mime.EncodedWordDecoder.Decode(m.FromHeader));
                if (from.Count > 0 && Matches(from[0].DisplayName, from[0].Address, w))
                {
                    found.TryAdd(from[0].Address, from[0].DisplayName.Length > 0 ? from[0].DisplayName : from[0].Address);
                }
            }
        }
        var ranked = new List<(string Address, string Name, int Sent)>();
        foreach (KeyValuePair<string, string> f in found.Take(8))
        {
            ContactTraffic t = await this.ContactTrafficAsync(personId, f.Key, ct).ConfigureAwait(false);
            ranked.Add((f.Key, f.Value, t.Sent + t.Received));
        }
        return ranked.OrderByDescending(r => r.Sent).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).Take(5).Select(r => (r.Address, r.Name)).ToList();

        static bool Matches(string name, string address, string w) =>
            address.Split('@')[0].Equals(w, StringComparison.OrdinalIgnoreCase)
            || address.StartsWith(w, StringComparison.OrdinalIgnoreCase)
            || name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(part => part.Trim('.').Equals(w, StringComparison.OrdinalIgnoreCase) || part.StartsWith(w, StringComparison.OrdinalIgnoreCase))
            || name.Equals(w, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The person's newest mail from an address, in every folder but Trash and Junk (design review [8g]).</summary>
    /// <param name="personId">The person.</param>
    /// <param name="address">The sender's address.</param>
    /// <param name="count">How many.</param>
    /// <param name="withFiles">Only those with attachments.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The messages, newest first.</returns>
    public async Task<IReadOnlyList<LajnaMail>> MailFromAsync(Guid personId, string address, int count, bool withFiles, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        (IReadOnlyList<MessageRow> items, _) = await this.SearchFilteredAsync(personId, null, string.Empty, address, null, null, withFiles, 0, Math.Clamp(count, 1, 20), ct: ct).ConfigureAwait(false);
        return items.Select(m => new LajnaMail(m.Id, Anjal.Mime.EncodedWordDecoder.Decode(m.FromHeader), m.Subject, m.Preview, m.ReceivedAt, !m.Seen, m.HasAttachments)).ToList();
    }

    /// <summary>
    /// Who covered for the person when they were last away (owner, 12 Oct 2026: "who covered last
    /// time - you will tell me how it will find"). Anjal reads the person's own Sent mail only: the
    /// newest messages about leave, holiday or being away, and in them a sentence that names who
    /// covers ("Dr Rao will cover", "in my absence please contact Meera", "covering for me: ...").
    /// When none says so, a colleague copied on the leave mail is offered, marked as only copied.
    /// The answer names the mail it came from, so the person can check it.
    /// </summary>
    /// <param name="personId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Who, or null when their mail does not say.</returns>
    public async Task<LajnaCover?> WhoCoveredAsync(Guid personId, CancellationToken ct = default)
    {
        FolderRow? sent = await this.GetFolderAsync(personId, "Sent", ct).ConfigureAwait(false);
        if (sent is null)
        {
            return null;
        }
        var leaveMail = new Dictionary<Guid, MessageRow>();
        foreach (string word in LeaveWords)
        {
            (IReadOnlyList<MessageRow> items, _) = await this.SearchFilteredAsync(personId, sent.Id, word, string.Empty, null, null, false, 0, 10, ct: ct).ConfigureAwait(false);
            foreach (MessageRow m in items)
            {
                leaveMail.TryAdd(m.Id, m);
            }
        }
        IReadOnlyList<(string Address, string Name)> colleagues = await this.ColleaguesAsync(personId, ct).ConfigureAwait(false);
        LajnaCover? copied = null;
        foreach (MessageRow m in leaveMail.Values.OrderByDescending(m => m.ReceivedAt).Take(6))
        {
            MessageView? view = await this.PeekAsync(personId, m.Id, ct).ConfigureAwait(false);
            if (view is null)
            {
                continue;
            }
            string text = HtmlText.ToPlain(view.BodyHtml);
            // Only the person's own words: stop at the first quoted reply.
            int quote = QuoteStart().Match(text) is { Success: true } q ? q.Index : -1;
            if (quote > 0)
            {
                text = text[..quote];
            }
            if (CoverName(text, colleagues) is string name)
            {
                return new LajnaCover(name, m.ReceivedAt, view.Subject, false);
            }
            if (copied is null)
            {
                foreach (Anjal.Mime.MailAddress cc in Anjal.Mime.AddressParser.Parse(Anjal.Mime.EncodedWordDecoder.Decode(view.Cc)))
                {
                    (string Address, string Name) colleague = colleagues.FirstOrDefault(c => c.Address.Equals(cc.Address, StringComparison.OrdinalIgnoreCase));
                    if (colleague.Address is not null)
                    {
                        copied = new LajnaCover(colleague.Name.Length > 0 ? colleague.Name : colleague.Address, m.ReceivedAt, view.Subject, true);
                        break;
                    }
                }
            }
        }
        return copied;
    }

    private static readonly string[] LeaveWords = { "leave", "vacation", "holiday", "out of office", "away" };

    /// <summary>The name a text gives for who covers, or null (public for tests).</summary>
    /// <param name="text">The person's own words.</param>
    /// <param name="colleagues">The organisation's people, to prefer a real colleague's name.</param>
    /// <returns>The name.</returns>
    public static string? CoverName(string text, IReadOnlyList<(string Address, string Name)> colleagues)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(colleagues);
        foreach (Regex r in CoverPatterns)
        {
            Match m = r.Match(text);
            if (!m.Success)
            {
                continue;
            }
            string name = m.Groups["name"].Value.Trim().TrimEnd('.', ',');
            if (name.Contains('@', StringComparison.Ordinal))
            {
                (string Address, string Name) byAddress = colleagues.FirstOrDefault(c => c.Address.Equals(name, StringComparison.OrdinalIgnoreCase));
                return byAddress.Address is null ? name : byAddress.Name.Length > 0 ? byAddress.Name : byAddress.Address;
            }
            if (name.Length < 2 || NotNames.Contains(name.Split(' ')[0]))
            {
                continue;
            }
            (string Address, string Name) known = colleagues.FirstOrDefault(c => c.Name.Length > 0 && (c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || c.Name.Contains(name, StringComparison.OrdinalIgnoreCase)));
            return known.Address is null ? name : known.Name;
        }
        return null;
    }

    private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase) { "I", "We", "The", "My", "Our", "Someone", "Anyone", "Please", "This", "He", "She", "They", "You", "It" };

    // A name as people write it - capital letters, an optional title - matched with case kept even
    // where the words around it are matched without case; or an address.
    private const string NamePart = @"(?-i:(?<name>(?:(?:Dr|Mr|Ms|Mrs)\.?\s+)?\p{Lu}[\p{L}'-]+(?:[ ]\p{Lu}[\p{L}'-]+){0,2}|[\w.+-]+@[\w.-]+\.\w+))";

    private static readonly Regex[] CoverPatterns =
    {
        new(NamePart + @"\s+(?:will|would|has kindly agreed to|has agreed to|is going to|shall|can)\s+(?:be\s+)?(?:cover|covering|look after|looking after|handle|handling|take care of|attend to)", RegexOptions.CultureInvariant),
        new(@"(?:cover(?:ing)? for me|in my absence|while I am away|while I'm away|during my leave|during my absence)[^.\n]{0,40}?(?:please\s+)?(?:contact|reach|write to|is|will be|:|-)\s*" + NamePart, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
        new(@"(?:please\s+)?(?:contact|reach|write to)\s+" + NamePart + @"[^.\n]{0,40}?(?:in my absence|while I am away|while I'm away|during my leave|until I am back|until I return)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
    };

    /// <summary>
    /// What happened to the person's own newest message to an address (design review [8d]), from the
    /// queue and the attempts kept beside the original; null when they sent none in 30 days.
    /// </summary>
    /// <param name="personId">The person.</param>
    /// <param name="address">The recipient.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The story, or null.</returns>
    public async Task<LajnaStory?> OwnStoryAsync(Guid personId, string address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (await this.GetContextAsync(personId, ct).ConfigureAwait(false) is not { } context)
        {
            return null;
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<OutboundMessage> mine = await this.messageStore.ListOutboundBySendersAsync(new[] { context.Mailbox.Address }, now.AddDays(-30), now.AddMinutes(1), 500, ct).ConfigureAwait(false);
        OutboundMessage? m = mine.FirstOrDefault(o => o.EnvelopeTo.Trim().Equals(address.Trim(), StringComparison.OrdinalIgnoreCase));
        if (m is not null)
        {
            return await this.StoryOfAsync(m, null, ct).ConfigureAwait(false);
        }
        // Mail to someone in the organisation never waits in the queue: it is filed at once.
        FolderRow? sent = await this.GetFolderAsync(personId, "Sent", ct).ConfigureAwait(false);
        if (sent is not null)
        {
            (IReadOnlyList<MessageRow> items, _) = await this.SearchFilteredAsync(personId, sent.Id, address, string.Empty, now.AddDays(-30), null, false, 0, 5, ct: ct).ConfigureAwait(false);
            if (items.Count > 0 && items[0] is MessageRow s)
            {
                return new LajnaStory("Inside", address, s.Subject, s.ReceivedAt, 0, string.Empty, null, s.ReceivedAt, Array.Empty<SendingAttempt>(), null);
            }
        }
        return null;
    }

    /// <summary>For an administrator: what happened to the organisation's applications' newest message to an address (design review [8d]).</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="address">The recipient.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The story, or null.</returns>
    public async Task<LajnaStory?> AppStoryAsync(TenantRow tenant, string address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(address);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SendingRow? row = (await this.SendingRowsAsync(tenant, now.AddDays(-7), now.AddMinutes(1), ct).ConfigureAwait(false))
            .FirstOrDefault(r => r.To.Trim().Equals(address.Trim(), StringComparison.OrdinalIgnoreCase));
        if (row is null || await this.SendingDetailAsync(tenant, row.Id, ct).ConfigureAwait(false) is not SendingDetail d)
        {
            return null;
        }
        return new LajnaStory(d.Row.Status, d.Row.To, d.Row.Subject, d.Row.At, d.Row.Tries, d.Row.LastError, d.Row.Next, d.Row.GiveUp, d.Attempts, d.Row.App);
    }

    private async Task<LajnaStory> StoryOfAsync(OutboundMessage m, string? app, CancellationToken ct)
    {
        var attempts = new List<SendingAttempt>();
        if (this.messageStore is IEvidenceStore evidence)
        {
            (Guid? evidenceId, _) = await evidence.GetOutboundEvidenceLinkAsync(m.Id, ct).ConfigureAwait(false);
            if (evidenceId is Guid e)
            {
                attempts.AddRange((await evidence.ListEvidenceAttemptsAsync(e, ct).ConfigureAwait(false))
                    .OrderBy(a => a.AttemptedAt)
                    .Select(a => new SendingAttempt(a.AttemptedAt, a.RemoteHost, a.TransportTls, a.ReplyCode, a.ReplyText, a.Outcome)));
            }
        }
        string status = SendingStatus(m);
        // The queued row keeps only its first 8 KB when listed; the subject is in the headers.
        return new LajnaStory(status, m.EnvelopeTo, HeaderOf(m.RawBytes, "Subject"), m.CreatedAt, m.Attempts, m.LastError,
            status is "Delayed" or "Queued" ? m.NextAttemptAt : null, m.GiveUpAt, attempts, app);
    }

    /// <summary>The names of the organisation's administrators, for "ask your administrator".</summary>
    /// <param name="personId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Their names (or addresses).</returns>
    public async Task<IReadOnlyList<string>> AdministratorNamesAsync(Guid personId, CancellationToken ct = default)
    {
        if (await this.GetContextAsync(personId, ct).ConfigureAwait(false) is not { } context)
        {
            return Array.Empty<string>();
        }
        var names = new List<string>();
        foreach (MailboxRow m in await this.store.ListMailboxesAsync(context.Tenant.Id, ct).ConfigureAwait(false))
        {
            if (m.Enabled && await this.IsOrgAdminAsync(m.Id, ct).ConfigureAwait(false))
            {
                names.Add(m.DisplayName.Length > 0 ? m.DisplayName + " (" + m.Address + ")" : m.Address);
            }
        }
        return names;
    }

    [GeneratedRegex(@"(?m)^(?:On .{4,120} wrote:|-{2,}\s*Original Message|>)", RegexOptions.CultureInvariant)]
    private static partial Regex QuoteStart();
}
