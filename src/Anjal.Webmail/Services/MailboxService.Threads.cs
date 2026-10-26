using System.Collections.Concurrent;
using System.Globalization;
using Anjal.Mime;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>One mail of a conversation as the reader shows it (rc.24, brief 10a and 10f).</summary>
/// <param name="Id">The message.</param>
/// <param name="Letter">A, B, C … in time order.</param>
/// <param name="From">The sender's name (or address).</param>
/// <param name="FromAddress">The sender's address.</param>
/// <param name="IsOwn">True when the person sent it.</param>
/// <param name="At">When it arrived or was sent.</param>
/// <param name="Preview">The start of its text.</param>
/// <param name="Seen">Read.</param>
/// <param name="Files">True when it has attachments.</param>
/// <param name="Folder">Its folder.</param>
/// <param name="ParentId">The mail it answers, from In-Reply-To or References only; null when none is in the conversation.</param>
/// <param name="LinkUnknown">True when it carries neither header and is not the first mail: placed by time, "reply link unknown".</param>
/// <param name="Depth">Its depth in the reply tree (0 for a mail that answers none here).</param>
/// <param name="MainLine">True on the conversation's main line (the first mail and, from each mail, its earliest reply).</param>
public sealed record ThreadMail(Guid Id, string Letter, string From, string FromAddress, bool IsOwn, DateTimeOffset At, string Preview, bool Seen, bool Files, string Folder, Guid? ParentId, bool LinkUnknown, int Depth, bool MainLine);

/// <summary>Someone in a conversation (rc.24, 10a "This conversation").</summary>
/// <param name="Name">Their name, or address.</param>
/// <param name="Address">Their address.</param>
/// <param name="IsYou">True for the person reading.</param>
/// <param name="Mails">How many of the conversation's mails they sent.</param>
/// <param name="JoinedAt">The number (1-based, time order) of the first mail they appear on.</param>
public sealed record ThreadPerson(string Name, string Address, bool IsYou, int Mails, int JoinedAt);

/// <summary>A file in a conversation (rc.24, 10a "All files in this conversation").</summary>
/// <param name="MessageId">The mail it came with.</param>
/// <param name="Index">Its index among that mail's attachments.</param>
/// <param name="Name">Its file name.</param>
/// <param name="ContentType">Its type.</param>
/// <param name="SizeBytes">Its size.</param>
/// <param name="From">Who sent it.</param>
public sealed record ThreadFile(Guid MessageId, int Index, string Name, string ContentType, long SizeBytes, string From);

/// <summary>A conversation as the reader shows it (rc.24): its mails in time order, who is in it, its files, and who answered whom.</summary>
public sealed class ThreadView
{
    /// <summary>The mails, oldest first.</summary>
    public IReadOnlyList<ThreadMail> Mails { get; init; } = Array.Empty<ThreadMail>();

    /// <summary>The people, in the order they joined.</summary>
    public IReadOnlyList<ThreadPerson> People { get; init; } = Array.Empty<ThreadPerson>();

    /// <summary>Every file of every mail, oldest first.</summary>
    public IReadOnlyList<ThreadFile> Files { get; init; } = Array.Empty<ThreadFile>();

    /// <summary>The mail with this id, or null.</summary>
    /// <param name="id">The message.</param>
    public ThreadMail? Get(Guid id) => this.Mails.FirstOrDefault(m => m.Id == id);

    /// <summary>The mails that answer this one, oldest first.</summary>
    /// <param name="id">The message.</param>
    public IReadOnlyList<ThreadMail> RepliesTo(Guid id) => this.Mails.Where(m => m.ParentId == id).ToList();

    /// <summary>The other mails that answer the same mail as this one, oldest first.</summary>
    /// <param name="id">The message.</param>
    public IReadOnlyList<ThreadMail> AlsoReplied(Guid id) =>
        this.Get(id) is { ParentId: Guid parent } ? this.Mails.Where(m => m.ParentId == parent && m.Id != id).ToList() : Array.Empty<ThreadMail>();

    /// <summary>True when the person has answered this mail.</summary>
    /// <param name="id">The message.</param>
    public bool YouReplied(Guid id) => this.Mails.Any(m => m.ParentId == id && m.IsOwn);

    /// <summary>True when the conversation splits somewhere (a mail answered more than once).</summary>
    public bool Branches => this.Mails.Where(m => m.ParentId is not null).GroupBy(m => m.ParentId).Any(g => g.Count() > 1);

    /// <summary>The first mail answered more than once, or null.</summary>
    public ThreadMail? FirstSplit =>
        this.Mails.FirstOrDefault(m => this.Mails.Count(r => r.ParentId == m.Id) > 1);

    /// <summary>
    /// The mails in reply-tree order (10f "Reply tree"): each mail followed by its answers,
    /// oldest first within a branch; a mail with no parent here starts a new root, in time order.
    /// </summary>
    public IReadOnlyList<ThreadMail> TreeOrder()
    {
        var order = new List<ThreadMail>(this.Mails.Count);
        var seen = new HashSet<Guid>();
        void Visit(ThreadMail m)
        {
            if (!seen.Add(m.Id))
            {
                return;
            }
            order.Add(m);
            foreach (ThreadMail child in this.Mails.Where(c => c.ParentId == m.Id))
            {
                Visit(child);
            }
        }
        foreach (ThreadMail root in this.Mails.Where(m => m.ParentId is null))
        {
            Visit(root);
        }
        foreach (ThreadMail rest in this.Mails)
        {
            Visit(rest);
        }
        return order;
    }
}

/// <summary>
/// Conversations by who answered whom (rc.24, item 68; brief 10a and 10f). Which mails belong
/// together is still the subject's conversation (rc.12); the tree inside it is built from the
/// In-Reply-To and References headers only, never from the subject or the time. The headers are
/// read from each mail's stored copy once and kept, since a stored mail never changes.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>Mails beyond this many in a conversation are not threaded (the newest are kept).</summary>
    public const int ThreadLimit = 200;

    private const int ThreadLinkCacheLimit = 50_000;

    private static readonly ConcurrentDictionary<Guid, ThreadLink> ThreadLinks = new();

    private sealed record ThreadLink(string MessageId, string InReplyTo, IReadOnlyList<string> References, string Cc, IReadOnlyList<AttachmentView> Files);

    /// <summary>The conversation a message belongs to, threaded (rc.24); a single mail when it stands alone.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="message">The message.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The conversation.</returns>
    public async Task<ThreadView> ThreadOfAsync(Guid mailboxId, MessageRow message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        IReadOnlyList<ConversationItem> items = await this.ConversationOfAsync(mailboxId, message, ct).ConfigureAwait(false);
        List<Guid> ids = items.Count > 0 ? items.Select(i => i.Id).ToList() : new List<Guid> { message.Id };
        if (!ids.Contains(message.Id))
        {
            ids.Add(message.Id);
        }
        if (ids.Count > ThreadLimit)
        {
            ids = items.OrderByDescending(i => i.At).Take(ThreadLimit).Select(i => i.Id).Union(new[] { message.Id }).ToList();
        }
        string me = (await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false))?.Mailbox.Address ?? string.Empty;
        IReadOnlyDictionary<Guid, string> folderNames = await this.FolderNamesAsync(mailboxId, ct).ConfigureAwait(false);
        var rows = new List<(MessageRow Row, ThreadLink Link)>();
        foreach (Guid id in ids)
        {
            MessageRow? row = id == message.Id ? message : await this.GetOwnedRowAsync(mailboxId, id, ct).ConfigureAwait(false);
            if (row is null)
            {
                continue;
            }
            rows.Add((row, await this.ThreadLinkAsync(mailboxId, row, ct).ConfigureAwait(false)));
        }
        rows.Sort((a, b) => a.Row.ReceivedAt.CompareTo(b.Row.ReceivedAt));
        return BuildThread(OneCopyEach(rows, message, folderNames), me, folderNames);
    }

    /// <summary>
    /// rc.24.1 (owner, 26 Oct 2026: "F and G came - how it came twice?"): a mail sent to oneself is kept in
    /// Sent and delivered to the Inbox - two copies with one Message-ID. A conversation shows it once: the
    /// copy that is open, else the copy in the open mail's folder, else the first kept.
    /// </summary>
    private static List<(MessageRow Row, ThreadLink Link)> OneCopyEach(List<(MessageRow Row, ThreadLink Link)> rows, MessageRow open, IReadOnlyDictionary<Guid, string> folderNames)
    {
        string openFolder = folderNames.TryGetValue(open.FolderId, out string? f) ? f : string.Empty;
        var kept = new List<(MessageRow Row, ThreadLink Link)>(rows.Count);
        foreach (IGrouping<string, (MessageRow Row, ThreadLink Link)> same in rows.GroupBy(r => r.Link.MessageId.Length > 0 ? r.Link.MessageId.ToLowerInvariant() : r.Row.Id.ToString("N")))
        {
            (MessageRow Row, ThreadLink Link) pick = same.FirstOrDefault(r => r.Row.Id == open.Id);
            if (pick.Row is null)
            {
                pick = same.FirstOrDefault(r => folderNames.TryGetValue(r.Row.FolderId, out string? n) && n == openFolder);
            }
            kept.Add(pick.Row is null ? same.First() : pick);
        }
        kept.Sort((a, b) => a.Row.ReceivedAt.CompareTo(b.Row.ReceivedAt));
        return kept;
    }

    private static ThreadView BuildThread(List<(MessageRow Row, ThreadLink Link)> rows, string me, IReadOnlyDictionary<Guid, string> folderNames)
    {
        // Who answers whom: In-Reply-To first, then the nearest References entry that is here.
        var byMessageId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach ((MessageRow row, ThreadLink link) in rows)
        {
            if (link.MessageId.Length > 0)
            {
                byMessageId.TryAdd(link.MessageId, row.Id);
            }
        }
        var parent = new Dictionary<Guid, Guid?>();
        var unknown = new HashSet<Guid>();
        for (int i = 0; i < rows.Count; i++)
        {
            (MessageRow row, ThreadLink link) = rows[i];
            Guid? p = null;
            if (link.InReplyTo.Length > 0 && byMessageId.TryGetValue(link.InReplyTo, out Guid a) && a != row.Id)
            {
                p = a;
            }
            else
            {
                for (int r = link.References.Count - 1; r >= 0 && p is null; r--)
                {
                    if (byMessageId.TryGetValue(link.References[r], out Guid b) && b != row.Id)
                    {
                        p = b;
                    }
                }
            }
            // A parent must be older; a loop in bad headers is broken here.
            if (p is Guid pp && rows.FindIndex(x => x.Row.Id == pp) > i)
            {
                p = null;
            }
            parent[row.Id] = p;
            if (p is null && i > 0 && link.InReplyTo.Length == 0 && link.References.Count == 0)
            {
                unknown.Add(row.Id);
            }
        }

        // The main line: from the first mail, each mail's earliest answer.
        var main = new HashSet<Guid>();
        if (rows.Count > 0)
        {
            Guid? at = rows[0].Row.Id;
            while (at is Guid cur && main.Add(cur))
            {
                at = rows.Select(x => x.Row.Id).FirstOrDefault(id => parent[id] == cur) is Guid next && next != Guid.Empty ? next : null;
            }
        }

        var depth = new Dictionary<Guid, int>();
        int DepthOf(Guid id)
        {
            if (depth.TryGetValue(id, out int d))
            {
                return d;
            }
            d = parent[id] is Guid p ? DepthOf(p) + 1 : 0;
            depth[id] = d;
            return d;
        }

        var mails = new List<ThreadMail>(rows.Count);
        var people = new List<ThreadPerson>();
        var files = new List<ThreadFile>();
        for (int i = 0; i < rows.Count; i++)
        {
            (MessageRow row, ThreadLink link) = rows[i];
            string folder = folderNames.TryGetValue(row.FolderId, out string? f) ? f : string.Empty;
            IReadOnlyList<MailAddress> senders = AddressParser.Parse(EncodedWordDecoder.Decode(row.FromHeader));
            MailAddress? sender = senders.Count > 0 ? senders[0] : null;
            string address = sender?.Address ?? row.EnvelopeFrom;
            string name = sender is { DisplayName.Length: > 0 } ? sender.DisplayName : address;
            bool own = (me.Length > 0 && string.Equals(address, me, StringComparison.OrdinalIgnoreCase)) || folder is "Sent" or "Drafts" or ScheduledFolder;
            mails.Add(new ThreadMail(row.Id, LetterOf(i), name, address, own, row.ReceivedAt, row.Preview, row.Seen, row.HasAttachments || link.Files.Count > 0, folder,
                parent[row.Id], unknown.Contains(row.Id), DepthOf(row.Id), main.Contains(row.Id)));

            // Everyone on the mail, in order: sender, then To and Cc.
            var on = new List<MailAddress>();
            if (sender is not null)
            {
                on.Add(sender);
            }
            on.AddRange(AddressParser.Parse(EncodedWordDecoder.Decode(row.ToHeader)));
            on.AddRange(AddressParser.Parse(EncodedWordDecoder.Decode(link.Cc)));
            foreach (MailAddress who in on)
            {
                int at = people.FindIndex(x => string.Equals(x.Address, who.Address, StringComparison.OrdinalIgnoreCase));
                bool you = (me.Length > 0 && string.Equals(who.Address, me, StringComparison.OrdinalIgnoreCase)) || (own && ReferenceEquals(who, sender));
                if (at < 0)
                {
                    people.Add(new ThreadPerson(who.DisplayName.Length > 0 ? who.DisplayName : who.Address, who.Address, you, 0, i + 1));
                }
                else if (who.DisplayName.Length > 0 && people[at].Name == people[at].Address)
                {
                    people[at] = people[at] with { Name = who.DisplayName };
                }
            }
            int s = people.FindIndex(x => string.Equals(x.Address, address, StringComparison.OrdinalIgnoreCase));
            if (s >= 0)
            {
                people[s] = people[s] with { Mails = people[s].Mails + 1, IsYou = people[s].IsYou || own };
            }
            foreach (AttachmentView file in link.Files)
            {
                files.Add(new ThreadFile(row.Id, file.Index, file.FileName, file.ContentType, file.SizeBytes, own ? string.Empty : name));
            }
        }
        return new ThreadView { Mails = mails, People = people, Files = files };
    }

    /// <summary>A, B … Z, then AA, AB …: the letter of the mail at this place in time order.</summary>
    /// <param name="index">Zero-based.</param>
    /// <returns>The letter.</returns>
    public static string LetterOf(int index)
    {
        string s = string.Empty;
        int n = index + 1;
        while (n > 0)
        {
            n--;
            s = (char)('A' + (n % 26)) + s;
            n /= 26;
        }
        return s;
    }

    private async Task<ThreadLink> ThreadLinkAsync(Guid mailboxId, MessageRow row, CancellationToken ct)
    {
        if (ThreadLinks.TryGetValue(row.Id, out ThreadLink? known))
        {
            return known;
        }
        ThreadLink link = new(row.MessageId.Trim('<', '>', ' '), string.Empty, Array.Empty<string>(), string.Empty, Array.Empty<AttachmentView>());
        (MessageRow Row, FolderRow Folder, byte[] Raw)? loaded = await this.ReadRawAsync(mailboxId, row.Id, ct).ConfigureAwait(false);
        if (loaded is not null)
        {
            MimeMessage? parsed = null;
            try
            {
                parsed = MimeParser.Parse(loaded.Value.Raw);
            }
#pragma warning disable CA1031 // A mail that cannot be parsed simply has no links.
            catch (Exception)
            {
                parsed = null;
            }
#pragma warning restore CA1031
            if (parsed is not null)
            {
                MimePart? html = null;
                MimePart? text = null;
                var attachments = new List<(MimePart Part, AttachmentView View)>();
                Walk(parsed.Body, ref html, ref text, attachments);
                link = new ThreadLink(
                    (parsed.MessageId ?? row.MessageId).Trim('<', '>', ' '),
                    LastOf(MessageIds(parsed.Headers.Get("In-Reply-To"))),
                    MessageIds(parsed.Headers.Get("References")),
                    parsed.Headers.Get("Cc") ?? string.Empty,
                    attachments.Select(a => a.View).ToList());
            }
        }
        if (ThreadLinks.Count > ThreadLinkCacheLimit)
        {
            ThreadLinks.Clear();
        }
        ThreadLinks[row.Id] = link;
        return link;
    }

    private static string LastOf(IReadOnlyList<string> ids) => ids.Count > 0 ? ids[^1] : string.Empty;

    /// <summary>The message ids in a References or In-Reply-To value, in order, without angle brackets.</summary>
    /// <param name="value">The header value.</param>
    /// <returns>The ids.</returns>
    public static IReadOnlyList<string> MessageIds(string? value)
    {
        var ids = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return ids;
        }
        int at = 0;
        while (at < value.Length)
        {
            int open = value.IndexOf('<', at);
            if (open < 0)
            {
                break;
            }
            int close = value.IndexOf('>', open + 1);
            if (close < 0)
            {
                break;
            }
            string id = value[(open + 1)..close].Trim();
            if (id.Length > 0 && IsValidMessageId(id))
            {
                ids.Add(id);
            }
            at = close + 1;
        }
        return ids;
    }

    /// <summary>
    /// The References header for a reply (rc.24, RFC 5322 §3.6.4): the original's References
    /// (or its In-Reply-To when it has none), then the original's own id, the newest 20 kept.
    /// </summary>
    /// <param name="originalReferences">The original's References header, or empty.</param>
    /// <param name="originalInReplyTo">The original's In-Reply-To header, or empty.</param>
    /// <param name="originalId">The original's Message-ID.</param>
    /// <returns>The ids, oldest first, without angle brackets.</returns>
    public static IReadOnlyList<string> ReferencesFor(string? originalReferences, string? originalInReplyTo, string? originalId)
    {
        List<string> chain = MessageIds(originalReferences).ToList();
        if (chain.Count == 0)
        {
            chain.AddRange(MessageIds(originalInReplyTo));
        }
        string id = (originalId ?? string.Empty).Trim().Trim('<', '>');
        if (id.Length > 0 && IsValidMessageId(id))
        {
            chain.Add(id);
        }
        List<string> distinct = chain.Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count > 20 ? distinct.Skip(distinct.Count - 20).ToList() : distinct;
    }

    /// <summary>
    /// "Reply to both" (rc.24, brief 10f): the Cc of a reply with the people of the other reply added -
    /// everyone in <paramref name="more"/> who is not already on To or Cc, in order, each once.
    /// </summary>
    /// <param name="to">The reply's To.</param>
    /// <param name="cc">The reply's Cc.</param>
    /// <param name="more">The other reply's people (its sender and recipients, without the person replying).</param>
    /// <returns>The new Cc.</returns>
    public static string MergeAddresses(string to, string cc, string more)
    {
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(cc);
        ArgumentNullException.ThrowIfNull(more);
        var have = new HashSet<string>(AddressParser.Parse(to).Concat(AddressParser.Parse(cc)).Select(a => a.Address), StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        foreach (MailAddress a in AddressParser.Parse(more))
        {
            if (a.Address.Length > 0 && have.Add(a.Address))
            {
                added.Add(a.DisplayName.Length > 0 ? $"{a.DisplayName} <{a.Address}>" : a.Address);
            }
        }
        if (added.Count == 0)
        {
            return cc;
        }
        return cc.Trim().Length > 0 ? cc.TrimEnd().TrimEnd(',') + ", " + string.Join(", ", added) : string.Join(", ", added);
    }

    /// <summary>How a mail is spoken of in a conversation: "B (you, Tue)".</summary>
    /// <param name="mail">The mail.</param>
    /// <param name="clock">The person's clock.</param>
    /// <param name="you">The word for "you".</param>
    /// <param name="now">Now.</param>
    /// <returns>The words.</returns>
    public static string ThreadRef(ThreadMail mail, ZonedClock clock, string you, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(mail);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(you);
        return string.Create(CultureInfo.InvariantCulture, $"{mail.Letter} ({(mail.IsOwn ? you : mail.From)}, {clock.ListDay(mail.At, now)})");
    }
}
