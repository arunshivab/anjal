using System.Collections.Concurrent;
using System.Text;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>A colleague one can chat with (rc.28, DES-21).</summary>
/// <param name="Id">Their mailbox.</param>
/// <param name="Name">Their name.</param>
/// <param name="Address">Their address.</param>
/// <param name="Status">"online", "away", "busy" or "offline".</param>
/// <param name="Note">Their line.</param>
public sealed record ChatPerson(Guid Id, string Name, string Address, string Status, string Note);

/// <summary>A chat in a person's list.</summary>
/// <param name="Room">The chat.</param>
/// <param name="Title">The colleague's name, or the group's.</param>
/// <param name="Others">The other members.</param>
/// <param name="Unread">Messages from others not read yet.</param>
public sealed record ChatSummary(ChatRoomRow Room, string Title, IReadOnlyList<ChatPerson> Others, int Unread);

/// <summary>One message as shown.</summary>
/// <param name="Id">Its id.</param>
/// <param name="SenderId">Its writer.</param>
/// <param name="SenderName">Their name.</param>
/// <param name="Text">The words; empty when removed.</param>
/// <param name="At">When.</param>
/// <param name="Mine">Written by the reader.</param>
/// <param name="Removed">Removed by its writer.</param>
public sealed record ChatLine(Guid Id, Guid SenderId, string SenderName, string Text, DateTimeOffset At, bool Mine, bool Removed);

/// <summary>An open chat.</summary>
/// <param name="Summary">The chat.</param>
/// <param name="Lines">Its messages, oldest first.</param>
/// <param name="SeenBy">Who has read the reader's last message: names (one-to-one) or a count (group).</param>
public sealed record ChatView(ChatSummary Summary, IReadOnlyList<ChatLine> Lines, IReadOnlyList<string> SeenBy);

/// <summary>rc.28 (DES-21): chat between the people of an organisation, and their online status.</summary>
public sealed partial class MailboxService
{
    /// <summary>The longest message.</summary>
    public const int ChatMax = 4000;

    /// <summary>The most people in a group.</summary>
    public const int ChatGroupMax = 50;

    /// <summary>A writer may remove a message for this long (D-348).</summary>
    public static readonly TimeSpan ChatRemoveWindow = TimeSpan.FromMinutes(15);

    /// <summary>Used within this long: online; longer, while a page is open: away.</summary>
    public static readonly TimeSpan PresenceOnline = TimeSpan.FromMinutes(5);

    /// <summary>Not heard from for this long: offline.</summary>
    public static readonly TimeSpan PresenceOffline = TimeSpan.FromMinutes(30);

    private static readonly ConcurrentDictionary<Guid, DateTimeOffset> PresenceTouched = new();

    private IChatStore? Chats => this.store as IChatStore;

    /// <summary>Whether a person can chat: an organisation's person, with chat switched on (D-349, D-350).</summary>
    /// <param name="personId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when they can.</returns>
    public async Task<bool> ChatOnAsync(Guid personId, CancellationToken ct = default) =>
        this.Chats is not null
        && await this.GetContextAsync(personId, ct).ConfigureAwait(false) is (TenantRow tenant, MailboxRow _)
        && !await this.IsIndividualAsync(personId, ct).ConfigureAwait(false)
        && (await this.MailPolicyOfAsync(tenant.Id, ct).ConfigureAwait(false)).Chat;

    /// <summary>A page was used: the person is online (written at most once a minute).</summary>
    /// <param name="personId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task TouchPresenceAsync(Guid personId, CancellationToken ct = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (this.Chats is not IChatStore chats || (PresenceTouched.TryGetValue(personId, out DateTimeOffset last) && now - last < TimeSpan.FromMinutes(1)))
        {
            return;
        }
        PresenceTouched[personId] = now;
        await chats.TouchPresenceAsync(personId, now, ct).ConfigureAwait(false);
    }

    /// <summary>The person chooses a status: "auto", "busy" or "away", and a line.</summary>
    /// <param name="personId">The person.</param>
    /// <param name="status">The status.</param>
    /// <param name="note">The line.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task SetPresenceAsync(Guid personId, string? status, string? note, CancellationToken ct = default)
    {
        if (this.Chats is not IChatStore chats)
        {
            return;
        }
        string s = status is PresenceRow.Busy or PresenceRow.Away ? status : PresenceRow.Auto;
        string n = (note ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        await chats.SetPresenceAsync(personId, s, n.Length > 80 ? n[..80] : n, ct).ConfigureAwait(false);
    }

    /// <summary>A person's own status row.</summary>
    /// <param name="personId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Their row.</returns>
    public async Task<PresenceRow> MyPresenceAsync(Guid personId, CancellationToken ct = default) =>
        this.Chats is IChatStore chats && (await chats.GetPresenceAsync(new[] { personId }, ct).ConfigureAwait(false)).TryGetValue(personId, out PresenceRow? row)
            ? row
            : new PresenceRow { MailboxId = personId };

    /// <summary>What a status row shows now: "online", "away", "busy" or "offline".</summary>
    /// <param name="row">The row, or null for someone never seen.</param>
    /// <param name="now">The time.</param>
    /// <returns>The word.</returns>
    public static string PresenceWord(PresenceRow? row, DateTimeOffset now)
    {
        if (row?.LastSeenAt is not DateTimeOffset seen || now - seen > PresenceOffline)
        {
            return "offline";
        }
        return row.Status switch
        {
            PresenceRow.Busy => "busy",
            PresenceRow.Away => "away",
            _ => now - seen <= PresenceOnline ? "online" : "away",
        };
    }

    /// <summary>The colleagues a person can chat with: the organisation's people, not its shared mailboxes.</summary>
    /// <param name="personId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The colleagues, by name.</returns>
    public async Task<IReadOnlyList<ChatPerson>> ChatColleaguesAsync(Guid personId, CancellationToken ct = default)
    {
        if (!await this.ChatOnAsync(personId, ct).ConfigureAwait(false) || await this.GetContextAsync(personId, ct).ConfigureAwait(false) is not (TenantRow tenant, MailboxRow _))
        {
            return Array.Empty<ChatPerson>();
        }
        Dictionary<Guid, SharedMailbox> shared = await this.SharedOfTenantAsync(tenant.Id, ct).ConfigureAwait(false);
        List<MailboxRow> people = (await this.store.ListMailboxesAsync(tenant.Id, ct).ConfigureAwait(false))
            .Where(m => m.Id != personId && m.Enabled && m.Kind == MailboxRow.PersonKind && !shared.ContainsKey(m.Id) && m.PasswordPbkdf2.Length > 0).ToList();
        return await this.PeopleAsync(people, ct).ConfigureAwait(false);
    }

    /// <summary>A person's chats, the latest first.</summary>
    /// <param name="personId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The chats.</returns>
    public async Task<IReadOnlyList<ChatSummary>> ChatListAsync(Guid personId, CancellationToken ct = default)
    {
        if (this.Chats is not IChatStore chats || !await this.ChatOnAsync(personId, ct).ConfigureAwait(false))
        {
            return Array.Empty<ChatSummary>();
        }
        var list = new List<ChatSummary>();
        foreach (ChatRoomRow room in await chats.ListChatRoomsAsync(personId, ct).ConfigureAwait(false))
        {
            list.Add(await this.SummaryAsync(chats, room, personId, ct).ConfigureAwait(false));
        }
        return list;
    }

    /// <summary>
    /// Start a chat: with one colleague (the chat the two already have, when there is one), or a named
    /// group. Everyone must be of the person's organisation.
    /// </summary>
    /// <param name="personId">The person starting it.</param>
    /// <param name="others">The colleagues.</param>
    /// <param name="name">A group's name.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The chat, or why not.</returns>
    public async Task<(Guid? Room, string? Error)> StartChatAsync(Guid personId, IReadOnlyCollection<Guid> others, string? name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(others);
        if (this.Chats is not IChatStore chats || await this.GetContextAsync(personId, ct).ConfigureAwait(false) is not (TenantRow tenant, MailboxRow _) || !await this.ChatOnAsync(personId, ct).ConfigureAwait(false))
        {
            return (null, "Chat is not on for your organisation.");
        }
        var allowed = (await this.ChatColleaguesAsync(personId, ct).ConfigureAwait(false)).Select(p => p.Id).ToHashSet();
        var picked = others.Where(allowed.Contains).Distinct().ToList();
        if (picked.Count == 0)
        {
            return (null, "Choose who to chat with.");
        }
        if (picked.Count + 1 > ChatGroupMax)
        {
            return (null, "A group has at most 50 people.");
        }
        string groupName = (name ?? string.Empty).Trim();
        if (picked.Count == 1 && groupName.Length == 0 && await chats.FindDirectChatAsync(personId, picked[0], ct).ConfigureAwait(false) is ChatRoomRow existing)
        {
            return (existing.Id, null);
        }
        bool group = picked.Count > 1 || groupName.Length > 0;
        ChatRoomRow room = await chats.AddChatRoomAsync(new ChatRoomRow
        {
            TenantId = tenant.Id,
            Name = groupName.Length > 60 ? groupName[..60] : groupName,
            IsGroup = group,
            CreatedBy = personId,
            CreatedAt = DateTimeOffset.UtcNow,
        }, picked.Append(personId).ToList(), ct).ConfigureAwait(false);
        return (room.Id, null);
    }

    /// <summary>An open chat for a member: its messages from when they joined, and who has seen theirs. Marks it read.</summary>
    /// <param name="personId">The reader.</param>
    /// <param name="roomId">The chat.</param>
    /// <param name="after">Only messages after this (polling); <see cref="DateTimeOffset.MinValue"/> for the last 200.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The chat, or null for one they are not in.</returns>
    public async Task<ChatView?> ChatAsync(Guid personId, Guid roomId, DateTimeOffset after, CancellationToken ct = default)
    {
        if (this.Chats is not IChatStore chats || await chats.GetChatRoomAsync(roomId, ct).ConfigureAwait(false) is not ChatRoomRow room
            || !await this.ChatOnAsync(personId, ct).ConfigureAwait(false))
        {
            return null;
        }
        IReadOnlyList<ChatMemberRow> members = await chats.ListChatMembersAsync(roomId, ct).ConfigureAwait(false);
        if (members.FirstOrDefault(m => m.MailboxId == personId && m.LeftAt is null) is not ChatMemberRow me)
        {
            return null;
        }
        DateTimeOffset from = after > me.JoinedAt ? after : me.JoinedAt.AddTicks(-1);
        IReadOnlyList<ChatMessageRow> rows = await chats.ListChatMessagesAsync(roomId, from, 200, ct).ConfigureAwait(false);
        Dictionary<Guid, string> names = await this.NamesAsync(members.Select(m => m.MailboxId), ct).ConfigureAwait(false);
        var lines = rows.Select(r => new ChatLine(r.Id, r.SenderId, names.GetValueOrDefault(r.SenderId, "?"), r.RemovedAt is null ? OpenChatBody(r.Body) : string.Empty, r.CreatedAt, r.SenderId == personId, r.RemovedAt is not null)).ToList();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await chats.MarkChatReadAsync(roomId, personId, now, ct).ConfigureAwait(false);
        // Seen: the others who have read past the reader's last message.
        DateTimeOffset? myLast = lines.LastOrDefault(l => l.Mine && !l.Removed)?.At;
        if (myLast is null && after != DateTimeOffset.MinValue)
        {
            myLast = (await chats.ListChatMessagesAsync(roomId, me.JoinedAt.AddTicks(-1), 200, ct).ConfigureAwait(false)).LastOrDefault(r => r.SenderId == personId && r.RemovedAt is null)?.CreatedAt;
        }
        IReadOnlyList<string> seen = myLast is DateTimeOffset last
            ? members.Where(m => m.MailboxId != personId && m.LeftAt is null && m.LastReadAt >= last).Select(m => names.GetValueOrDefault(m.MailboxId, "?")).ToList()
            : Array.Empty<string>();
        return new ChatView(await this.SummaryAsync(chats, room, personId, ct).ConfigureAwait(false), lines, seen);
    }

    /// <summary>Write in a chat. The words are sealed to every member's key and the organisation's recovery key.</summary>
    /// <param name="personId">The writer.</param>
    /// <param name="roomId">The chat.</param>
    /// <param name="text">The words.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Why not, or null.</returns>
    public async Task<string?> SendChatAsync(Guid personId, Guid roomId, string? text, CancellationToken ct = default)
    {
        string words = (text ?? string.Empty).Trim();
        if (words.Length == 0)
        {
            return "Write something first.";
        }
        if (words.Length > ChatMax)
        {
            return "That is too long for a chat. Send it as a mail.";
        }
        if (this.Chats is not IChatStore chats || await chats.GetChatRoomAsync(roomId, ct).ConfigureAwait(false) is not ChatRoomRow room
            || !await this.ChatOnAsync(personId, ct).ConfigureAwait(false))
        {
            return "Chat is not on for your organisation.";
        }
        IReadOnlyList<ChatMemberRow> members = await chats.ListChatMembersAsync(roomId, ct).ConfigureAwait(false);
        if (!members.Any(m => m.MailboxId == personId && m.LeftAt is null))
        {
            return "You are not in this chat.";
        }
        string body = await this.SealChatAsync(room.TenantId, members.Where(m => m.LeftAt is null).Select(m => m.MailboxId).ToList(), words, ct).ConfigureAwait(false);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await chats.AddChatMessageAsync(new ChatMessageRow { RoomId = roomId, SenderId = personId, Body = body, CreatedAt = now }, ct).ConfigureAwait(false);
        await chats.MarkChatReadAsync(roomId, personId, now, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>The writer removes a message within 15 minutes (D-348); the log keeps that it was removed.</summary>
    /// <param name="personId">The writer.</param>
    /// <param name="messageId">The message.</param>
    /// <param name="actor">Who, for the log.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when removed.</returns>
    public async Task<bool> RemoveChatAsync(Guid personId, Guid messageId, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (this.Chats is not IChatStore chats)
        {
            return false;
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!await chats.RemoveChatMessageAsync(messageId, personId, now, now - ChatRemoveWindow, ct).ConfigureAwait(false))
        {
            return false;
        }
        await this.messageStore.AppendAuditAsync(new AuditEvent { Actor = actor, Action = "chat.message-removed", Subject = actor, Detail = messageId.ToString("N") }, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Add colleagues to a group; they read from now on.</summary>
    /// <param name="personId">A member.</param>
    /// <param name="roomId">The group.</param>
    /// <param name="others">The colleagues.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Why not, or null.</returns>
    public async Task<string?> AddToChatAsync(Guid personId, Guid roomId, IReadOnlyCollection<Guid> others, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(others);
        if (this.Chats is not IChatStore chats || await chats.GetChatRoomAsync(roomId, ct).ConfigureAwait(false) is not { IsGroup: true }
            || !(await chats.ListChatMembersAsync(roomId, ct).ConfigureAwait(false)).Any(m => m.MailboxId == personId && m.LeftAt is null))
        {
            return "Only a group's members can add people to it.";
        }
        var allowed = (await this.ChatColleaguesAsync(personId, ct).ConfigureAwait(false)).Select(p => p.Id).ToHashSet();
        foreach (Guid id in others.Where(allowed.Contains).Distinct())
        {
            await chats.AddChatMemberAsync(roomId, id, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>Leave a group.</summary>
    /// <param name="personId">The member.</param>
    /// <param name="roomId">The group.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task LeaveChatAsync(Guid personId, Guid roomId, CancellationToken ct = default)
    {
        if (this.Chats is IChatStore chats && await chats.GetChatRoomAsync(roomId, ct).ConfigureAwait(false) is { IsGroup: true })
        {
            await chats.LeaveChatAsync(roomId, personId, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        }
    }

    /// <summary>How many chats have messages the person has not read (the rail's number).</summary>
    /// <param name="personId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The count.</returns>
    public async Task<int> UnreadChatsAsync(Guid personId, CancellationToken ct = default) =>
        this.Chats is IChatStore chats ? await chats.CountUnreadChatsAsync(personId, ct).ConfigureAwait(false) : 0;

    /// <summary>The status of people the reader sees (a mail's envelope): by address, for colleagues only.</summary>
    /// <param name="personId">The reader.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Each colleague's address and status word.</returns>
    public async Task<IReadOnlyDictionary<string, string>> ColleagueStatusAsync(Guid personId, CancellationToken ct = default) =>
        (await this.ChatColleaguesAsync(personId, ct).ConfigureAwait(false)).ToDictionary(p => p.Address, p => p.Status, StringComparer.OrdinalIgnoreCase);

    private async Task<ChatSummary> SummaryAsync(IChatStore chats, ChatRoomRow room, Guid personId, CancellationToken ct)
    {
        IReadOnlyList<ChatMemberRow> members = await chats.ListChatMembersAsync(room.Id, ct).ConfigureAwait(false);
        var otherRows = new List<MailboxRow>();
        foreach (ChatMemberRow m in members.Where(m => m.MailboxId != personId && m.LeftAt is null))
        {
            if (await this.store.GetMailboxByIdAsync(m.MailboxId, ct).ConfigureAwait(false) is MailboxRow row)
            {
                otherRows.Add(row);
            }
        }
        IReadOnlyList<ChatPerson> others = await this.PeopleAsync(otherRows, ct).ConfigureAwait(false);
        string title = room.Name.Length > 0 ? room.Name : others.Count == 0 ? "Only you" : string.Join(", ", others.Select(o => o.Name));
        DateTimeOffset read = members.FirstOrDefault(m => m.MailboxId == personId)?.LastReadAt ?? DateTimeOffset.MaxValue;
        int unread = room.LastMessageAt > read
            ? (await chats.ListChatMessagesAsync(room.Id, read, 100, ct).ConfigureAwait(false)).Count(m => m.SenderId != personId && m.RemovedAt is null)
            : 0;
        return new ChatSummary(room, title, others, unread);
    }

    private async Task<IReadOnlyList<ChatPerson>> PeopleAsync(IReadOnlyList<MailboxRow> people, CancellationToken ct)
    {
        IReadOnlyDictionary<Guid, PresenceRow> presence = this.Chats is IChatStore chats
            ? await chats.GetPresenceAsync(people.Select(p => p.Id).ToList(), ct).ConfigureAwait(false)
            : new Dictionary<Guid, PresenceRow>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return people.Select(p => new ChatPerson(p.Id, p.DisplayName.Trim().Length > 0 ? p.DisplayName.Trim() : p.Address, p.Address,
                PresenceWord(presence.GetValueOrDefault(p.Id), now), presence.GetValueOrDefault(p.Id)?.Note ?? string.Empty))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var names = new Dictionary<Guid, string>();
        foreach (Guid id in ids.Distinct())
        {
            if (await this.store.GetMailboxByIdAsync(id, ct).ConfigureAwait(false) is MailboxRow m)
            {
                names[id] = m.DisplayName.Trim().Length > 0 ? m.DisplayName.Trim() : m.Address;
            }
        }
        return names;
    }

    // "s1." + the words sealed to each member's public key and the organisation's recovery key; "p1." + the
    // words for an organisation whose mail is not encrypted.
    private async Task<string> SealChatAsync(Guid tenantId, IReadOnlyList<Guid> members, string words, CancellationToken ct)
    {
        if (this.store is not IMailKeyStore keys)
        {
            return "p1." + words;
        }
        var publicKeys = (await keys.GetPublicKeysAsync(members, ct).ConfigureAwait(false)).Values.ToList();
        if (publicKeys.Count == 0)
        {
            return "p1." + words;
        }
        if (await keys.GetTenantKeysAsync(tenantId, ct).ConfigureAwait(false) is TenantKeyRow tenant)
        {
            publicKeys.Add(tenant.RecoveryPublic);
        }
        return "s1." + MailEnvelope.Base64Url(MailEnvelope.SealToPublicKeys(Encoding.UTF8.GetBytes(words), publicKeys));
    }

    private static string OpenChatBody(string body)
    {
        if (body.StartsWith("p1.", StringComparison.Ordinal))
        {
            return body[3..];
        }
        if (!body.StartsWith("s1.", StringComparison.Ordinal))
        {
            return string.Empty;
        }
        try
        {
            byte[]? opened = MailEnvelope.Open(MailEnvelope.FromBase64Url(body[3..]), MailKeyring.Current);
            return opened is null ? string.Empty : Encoding.UTF8.GetString(opened);
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        {
            return string.Empty;
        }
    }
}
