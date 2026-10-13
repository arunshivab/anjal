using System.Globalization;
using Anjal.Mime;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>One unread message on Today, with why it is there.</summary>
/// <param name="Message">The message.</param>
/// <param name="Who">The sender's name, or address when there is no name.</param>
/// <param name="Address">The sender's address.</param>
/// <param name="Reason">"answer", "to" or "often" for Needs you; null otherwise.</param>
/// <param name="AnswersSubject">For an answer: the subject of the person's own message it answers.</param>
/// <param name="AnswersAt">For an answer: when the person sent that message.</param>
/// <param name="Expected">For an answer: the person had marked their message "Expect a reply".</param>
public sealed record TodayRow(MessageRow Message, string Who, string Address, string? Reason, string? AnswersSubject, DateTimeOffset? AnswersAt, bool Expected);

/// <summary>Today (design review section 8): the person's unread Inbox mail, what needs them first.</summary>
public sealed class TodayView
{
    /// <summary>Answers to the person's own mail, mail addressed to them (not Cc), and people they write to often.</summary>
    public IReadOnlyList<TodayRow> NeedsYou { get; init; } = Array.Empty<TodayRow>();

    /// <summary>The rest of the unread mail, apart from lists and notifications.</summary>
    public IReadOnlyList<TodayRow> Rest { get; init; } = Array.Empty<TodayRow>();

    /// <summary>Mailing lists and notifications, folded into one line.</summary>
    public IReadOnlyList<TodayRow> Lists { get; init; } = Array.Empty<TodayRow>();

    /// <summary>Every unread message in the Inbox, including those beyond the ones listed.</summary>
    public long Unread { get; init; }

    /// <summary>Mail that arrived since six in the evening yesterday, in the person's zone.</summary>
    public long ArrivedSinceEvening { get; init; }

    /// <summary>Of that, how many went to Junk.</summary>
    public long JunkSinceEvening { get; init; }

    /// <summary>The person's usual reply time, when "Your habits" is on and there were replies.</summary>
    public TimeSpan? UsualReply { get; init; }

    /// <summary>The rows in reading order: Needs you, then everything else, then the lists.</summary>
    public IEnumerable<TodayRow> InOrder => this.NeedsYou.Concat(this.Rest).Concat(this.Lists);
}

/// <summary>Today (rc.16, design review section 8, owner 11 Oct 2026).</summary>
public sealed partial class MailboxService
{
    /// <summary>The most unread messages Today lists; the count still says how many there are.</summary>
    public const int TodayLimit = 200;

    private static readonly char[] NameSeparators = { ' ', '.', '_' };

    private static readonly string[] Titles = { "dr", "mr", "mrs", "ms", "prof", "shri", "smt" };

    // The local parts of senders that only notify: they go with the lists, not with people.
    private static readonly string[] NotifierParts =
    {
        "noreply", "no-reply", "donotreply", "do-not-reply", "notification", "notifications", "notify", "alerts", "alert",
        "newsletter", "news", "mailer", "mailer-daemon", "bounce", "bounces", "postmaster", "updates", "digest", "info",
    };

    /// <summary>
    /// Today: the unread mail in the Inbox, sorted by who needs the person. "Needs you" holds
    /// answers to the person's own mail (the conversation has a message they sent before it),
    /// mail with them in To (not only Cc), and mail from people they wrote to at least three
    /// times in the last 90 days. Mailing lists and notifications (List-Id, Precedence: bulk or
    /// list, Auto-Submitted, or a sender such as no-reply@) are folded into one line. Every
    /// other unread message is "Everything else".
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="clock">The person's zone.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Today.</returns>
    public async Task<TodayView> TodayAsync(Guid mailboxId, ZonedClock clock, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        FolderRow? inbox = await this.GetFolderAsync(mailboxId, FolderRow.Inbox, ct).ConfigureAwait(false);
        if (context is null || inbox is null)
        {
            return new TodayView();
        }
        string me = context.Value.Mailbox.Address;
        long unreadCount = await this.UnreadInFolderAsync(mailboxId, inbox.Id, ct).ConfigureAwait(false);
        IReadOnlyList<MessageRow> unread = await this.store.ListMessagesBySeenAsync(mailboxId, inbox.Id, false, TodayLimit, 0, ct).ConfigureAwait(false);

        // People the person writes to: three or more messages to them in the last 90 days.
        IReadOnlyList<MessageFact> recent = await this.store.ListMessageFactsAsync(mailboxId, now.AddDays(-90), now.AddMinutes(1), 20000, ct).ConfigureAwait(false);
        var writtenTo = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (MessageFact f in recent.Where(f => f.Folder == "Sent"))
        {
            foreach (MailAddress a in AddressParser.Parse(EncodedWordDecoder.Decode(f.ToHeader)))
            {
                writtenTo[a.Address] = writtenTo.GetValueOrDefault(a.Address) + 1;
            }
        }

        IReadOnlyDictionary<string, IReadOnlyList<ConversationItem>> conversations = await this.ConversationIndexAsync(mailboxId, ct).ConfigureAwait(false);
        ReplyMarks marks = await this.GetReplyMarksAsync(mailboxId, ct).ConfigureAwait(false);
        var sentById = recent.Where(f => f.Folder == "Sent").ToDictionary(f => f.Id);

        var needs = new List<TodayRow>();
        var rest = new List<TodayRow>();
        var lists = new List<TodayRow>();
        foreach (MessageRow m in unread)
        {
            MailAddress? from = FirstAddress(m.FromHeader);
            string address = from?.Address ?? m.EnvelopeFrom;
            string who = from is { DisplayName.Length: > 0 } ? from.DisplayName : address;

            if (await this.IsListOrNotificationAsync(context.Value.Tenant, context.Value.Mailbox, m, address, ct).ConfigureAwait(false))
            {
                lists.Add(new TodayRow(m, who, address, null, null, null, false));
                continue;
            }

            // An answer: the conversation holds a message the person sent before this one.
            string key = ConversationKey(m.Subject);
            ConversationItem? mine = key.Length > 0 && conversations.TryGetValue(key, out IReadOnlyList<ConversationItem>? items)
                ? items.Where(i => i.Folder == "Sent" && i.At < m.ReceivedAt).OrderByDescending(i => i.At).FirstOrDefault()
                : null;
            if (mine is not null)
            {
                MessageFact? sentFact = sentById.GetValueOrDefault(mine.Id);
                bool expected = sentFact is not null && marks.Expect.Any(e => Math.Abs((e.At - sentFact.ReceivedAt).TotalMinutes) < 3
                    && string.Equals(e.Subject, EncodedWordDecoder.Decode(sentFact.Subject).Trim(), StringComparison.OrdinalIgnoreCase));
                needs.Add(new TodayRow(m, who, address, "answer", sentFact is null ? null : EncodedWordDecoder.Decode(sentFact.Subject).Trim(), mine.At, expected));
                continue;
            }
            bool toMe = AddressParser.Parse(EncodedWordDecoder.Decode(m.ToHeader)).Any(a => string.Equals(a.Address, me, StringComparison.OrdinalIgnoreCase));
            if (toMe && !IsAnjalItself(address))
            {
                needs.Add(new TodayRow(m, who, address, "to", null, null, false));
                continue;
            }
            if (writtenTo.GetValueOrDefault(address) >= 3)
            {
                needs.Add(new TodayRow(m, who, address, "often", null, null, false));
                continue;
            }
            rest.Add(new TodayRow(m, who, address, null, null, null, false));
        }

        // "Your morning": what arrived since six yesterday evening, and how much of it went to Junk.
        DateTimeOffset evening = AtLocal(clock, clock.Local(now).Date.AddDays(-1).AddHours(18));
        IEnumerable<MessageFact> since = recent.Where(f => f.ReceivedAt >= evening && f.Folder != "Sent" && f.Folder != "Drafts");
        long arrived = since.Count();
        long junk = since.Count(f => f.Folder == Anjal.Mailbox.MailboxSink.JunkFolder);
        TimeSpan? usual = null;
        if ((await this.GetMailSettingsAsync(mailboxId, ct).ConfigureAwait(false)).Habits)
        {
            usual = (await this.HabitsAsync(mailboxId, clock, now, ct).ConfigureAwait(false)).MedianReply;
        }

        return new TodayView
        {
            NeedsYou = needs,
            Rest = rest,
            Lists = lists,
            Unread = unreadCount,
            ArrivedSinceEvening = arrived,
            JunkSinceEvening = junk,
            UsualReply = usual,
        };
    }

    /// <summary>Whether a sender only notifies (no-reply@ and the like), from its address alone.</summary>
    /// <param name="address">The sender's address.</param>
    /// <returns>True for a notifying sender.</returns>
    public static bool IsNotifier(string? address)
    {
        if (string.IsNullOrEmpty(address) || !address.Contains('@', StringComparison.Ordinal))
        {
            return false;
        }
        string local = address[..address.IndexOf('@', StringComparison.Ordinal)].ToLowerInvariant();
        return NotifierParts.Any(p => local == p || local.StartsWith(p + "+", StringComparison.Ordinal) || local.StartsWith(p + "-", StringComparison.Ordinal) || local.StartsWith(p + ".", StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether a message came from a mailing list or a system that only notifies: List-Id,
    /// Precedence bulk/list/junk, or Auto-Submitted other than "no" in its headers, or a sender
    /// such as no-reply@. Only the headers are read.
    /// </summary>
    /// <param name="headerText">The message's header block.</param>
    /// <returns>True for a list or notification.</returns>
    public static bool HeadersSayList(string headerText)
    {
        ArgumentNullException.ThrowIfNull(headerText);
        foreach (string raw in headerText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            int colon = raw.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || raw[0] == ' ' || raw[0] == '\t')
            {
                continue;
            }
            string name = raw[..colon].Trim();
            string value = raw[(colon + 1)..].Trim().ToLowerInvariant();
            if (name.Equals("List-Id", StringComparison.OrdinalIgnoreCase) || name.Equals("List-Unsubscribe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (name.Equals("Precedence", StringComparison.OrdinalIgnoreCase) && value is "bulk" or "list" or "junk")
            {
                return true;
            }
            if (name.Equals("Auto-Submitted", StringComparison.OrdinalIgnoreCase) && value.Length > 0 && value != "no")
            {
                return true;
            }
        }
        return false;
    }

    // Anjal's own mails (alerts, sign-in notices) count as notifications, not as people.
    private static bool IsAnjalItself(string address) =>
        address.StartsWith("no-reply@", StringComparison.OrdinalIgnoreCase) || address.StartsWith("postmaster@", StringComparison.OrdinalIgnoreCase);

    private async Task<bool> IsListOrNotificationAsync(TenantRow tenant, MailboxRow mailbox, MessageRow m, string address, CancellationToken ct)
    {
        if (IsNotifier(address))
        {
            return true;
        }
        FolderRow? folder = await this.FolderByIdAsync(mailbox.Id, m.FolderId, ct).ConfigureAwait(false);
        if (folder is null)
        {
            return false;
        }
        byte[]? raw = await this.maildir.ReadAsync(tenant.Slug, mailbox.Address, folder.Name, m.MaildirFile, ct).ConfigureAwait(false);
        if (raw is null)
        {
            return false;
        }
        int end = HeaderEnd(raw);
        return HeadersSayList(System.Text.Encoding.UTF8.GetString(raw, 0, end));
    }

    // Where the header block ends: the first empty line, or at most 64 KB in.
    private static int HeaderEnd(byte[] raw)
    {
        int limit = Math.Min(raw.Length, 65536);
        for (int i = 0; i + 1 < limit; i++)
        {
            if (raw[i] == '\n' && (raw[i + 1] == '\n' || (raw[i + 1] == '\r' && i + 2 < limit && raw[i + 2] == '\n')))
            {
                return i;
            }
        }
        return limit;
    }

    /// <summary>"Good morning", "Good afternoon" or "Good evening" by the person's own clock.</summary>
    /// <param name="clock">The person's zone.</param>
    /// <param name="now">Now.</param>
    /// <returns>The greeting's words, to look up.</returns>
    public static string Greeting(ZonedClock clock, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(clock);
        int hour = clock.Local(now).Hour;
        return hour < 12 ? "Good morning, {name}" : hour < 17 ? "Good afternoon, {name}" : "Good evening, {name}";
    }

    /// <summary>A person's first name for a greeting: the first word of their name, or of their address.</summary>
    /// <param name="displayName">Their name.</param>
    /// <param name="address">Their address.</param>
    /// <returns>The first name.</returns>
    public static string FirstName(string? displayName, string? address)
    {
        string name = (displayName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            name = (address ?? string.Empty).Split('@')[0];
        }
        string[] parts = name.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries);
        string first = parts.Length > 0 ? parts[0] : name;
        // "Dr" and the like are titles, not names: "Dr. Anand Sharma" is Anand.
        string[] words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && Titles.Contains(words[0].TrimEnd('.').ToLowerInvariant()))
        {
            first = words[1];
        }
        return first.Length > 0 ? char.ToUpper(first[0], CultureInfo.InvariantCulture) + first[1..] : first;
    }
}
