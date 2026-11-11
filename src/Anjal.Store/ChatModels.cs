namespace Anjal.Store;

/// <summary>rc.28 (DES-21 §5): a chat - with one colleague, or a named group.</summary>
public sealed class ChatRoomRow
{
    /// <summary>Its id.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The organisation.</summary>
    public System.Guid TenantId { get; set; }

    /// <summary>A group's name; empty for a chat with one colleague.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>True for a group.</summary>
    public bool IsGroup { get; set; }

    /// <summary>Who started it.</summary>
    public System.Guid CreatedBy { get; set; }

    /// <summary>When.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }

    /// <summary>When its last message was written (its place in the list).</summary>
    public System.DateTimeOffset LastMessageAt { get; set; }
}

/// <summary>A person in a chat (rc.28).</summary>
public sealed class ChatMemberRow
{
    /// <summary>The chat.</summary>
    public System.Guid RoomId { get; set; }

    /// <summary>The person.</summary>
    public System.Guid MailboxId { get; set; }

    /// <summary>When they joined: they read from here.</summary>
    public System.DateTimeOffset JoinedAt { get; set; }

    /// <summary>When they left; null while in it.</summary>
    public System.DateTimeOffset? LeftAt { get; set; }

    /// <summary>Up to when they have read.</summary>
    public System.DateTimeOffset LastReadAt { get; set; }
}

/// <summary>One chat message (rc.28); its words sealed to the members' keys and the organisation's.</summary>
public sealed class ChatMessageRow
{
    /// <summary>Its id.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The chat.</summary>
    public System.Guid RoomId { get; set; }

    /// <summary>Who wrote it.</summary>
    public System.Guid SenderId { get; set; }

    /// <summary>The words, sealed (base64url envelope) - or plain for an organisation that is not encrypted.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>When it was written.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }

    /// <summary>When its writer removed it; null while it stands.</summary>
    public System.DateTimeOffset? RemovedAt { get; set; }
}

/// <summary>A person's online status (rc.28, DES-21 §1).</summary>
public sealed class PresenceRow
{
    /// <summary>Online by use (the default).</summary>
    public const string Auto = "auto";

    /// <summary>Do not disturb.</summary>
    public const string Busy = "busy";

    /// <summary>Shown as away whatever the use.</summary>
    public const string Away = "away";

    /// <summary>The person.</summary>
    public System.Guid MailboxId { get; set; }

    /// <summary>When a page of Anjal was last used.</summary>
    public System.DateTimeOffset? LastSeenAt { get; set; }

    /// <summary><see cref="Auto"/>, <see cref="Busy"/> or <see cref="Away"/>.</summary>
    public string Status { get; set; } = Auto;

    /// <summary>The person's line ("In theatre until 3").</summary>
    public string Note { get; set; } = string.Empty;
}

/// <summary>rc.28 (DES-21): what chat and online status need from the store.</summary>
public interface IChatStore
{
    /// <summary>Make a chat with its first members.</summary>
    /// <param name="room">The chat (its id is set when empty).</param>
    /// <param name="members">The people, the maker among them.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The chat.</returns>
    System.Threading.Tasks.Task<ChatRoomRow> AddChatRoomAsync(ChatRoomRow room, System.Collections.Generic.IReadOnlyCollection<System.Guid> members, System.Threading.CancellationToken ct = default);

    /// <summary>One chat.</summary>
    /// <param name="id">The chat.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The chat, or null.</returns>
    System.Threading.Tasks.Task<ChatRoomRow?> GetChatRoomAsync(System.Guid id, System.Threading.CancellationToken ct = default);

    /// <summary>The one-to-one chat of two people, if there is one.</summary>
    /// <param name="a">One.</param>
    /// <param name="b">The other.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The chat, or null.</returns>
    System.Threading.Tasks.Task<ChatRoomRow?> FindDirectChatAsync(System.Guid a, System.Guid b, System.Threading.CancellationToken ct = default);

    /// <summary>A person's chats, the latest first.</summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The chats.</returns>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<ChatRoomRow>> ListChatRoomsAsync(System.Guid mailboxId, System.Threading.CancellationToken ct = default);

    /// <summary>A chat's members, those who left included.</summary>
    /// <param name="roomId">The chat.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The members.</returns>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<ChatMemberRow>> ListChatMembersAsync(System.Guid roomId, System.Threading.CancellationToken ct = default);

    /// <summary>Add a person to a group (again, when they had left).</summary>
    /// <param name="roomId">The chat.</param>
    /// <param name="mailboxId">The person.</param>
    /// <param name="at">When.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    System.Threading.Tasks.Task AddChatMemberAsync(System.Guid roomId, System.Guid mailboxId, System.DateTimeOffset at, System.Threading.CancellationToken ct = default);

    /// <summary>A person leaves a group.</summary>
    /// <param name="roomId">The chat.</param>
    /// <param name="mailboxId">The person.</param>
    /// <param name="at">When.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    System.Threading.Tasks.Task LeaveChatAsync(System.Guid roomId, System.Guid mailboxId, System.DateTimeOffset at, System.Threading.CancellationToken ct = default);

    /// <summary>Keep a message; the chat moves to the top of its members' lists.</summary>
    /// <param name="message">The message (its id is set when empty).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The message.</returns>
    System.Threading.Tasks.Task<ChatMessageRow> AddChatMessageAsync(ChatMessageRow message, System.Threading.CancellationToken ct = default);

    /// <summary>A chat's messages written after a time (oldest first), at most <paramref name="limit"/> of the latest.</summary>
    /// <param name="roomId">The chat.</param>
    /// <param name="after">Only after this.</param>
    /// <param name="limit">The most to return.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The messages.</returns>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<ChatMessageRow>> ListChatMessagesAsync(System.Guid roomId, System.DateTimeOffset after, int limit, System.Threading.CancellationToken ct = default);

    /// <summary>The writer removes a message written after <paramref name="notBefore"/>; its words are deleted.</summary>
    /// <param name="messageId">The message.</param>
    /// <param name="senderId">Its writer.</param>
    /// <param name="at">When.</param>
    /// <param name="notBefore">Only a message written after this.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when removed.</returns>
    System.Threading.Tasks.Task<bool> RemoveChatMessageAsync(System.Guid messageId, System.Guid senderId, System.DateTimeOffset at, System.DateTimeOffset notBefore, System.Threading.CancellationToken ct = default);

    /// <summary>A person has read a chat up to a time.</summary>
    /// <param name="roomId">The chat.</param>
    /// <param name="mailboxId">The person.</param>
    /// <param name="at">Up to when.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    System.Threading.Tasks.Task MarkChatReadAsync(System.Guid roomId, System.Guid mailboxId, System.DateTimeOffset at, System.Threading.CancellationToken ct = default);

    /// <summary>How many of a person's chats have messages from others they have not read.</summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The count.</returns>
    System.Threading.Tasks.Task<int> CountUnreadChatsAsync(System.Guid mailboxId, System.Threading.CancellationToken ct = default);

    /// <summary>A page of Anjal was used now.</summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="at">When.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    System.Threading.Tasks.Task TouchPresenceAsync(System.Guid mailboxId, System.DateTimeOffset at, System.Threading.CancellationToken ct = default);

    /// <summary>The person chose a status and a line.</summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="status">The status.</param>
    /// <param name="note">The line.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    System.Threading.Tasks.Task SetPresenceAsync(System.Guid mailboxId, string status, string note, System.Threading.CancellationToken ct = default);

    /// <summary>People's statuses.</summary>
    /// <param name="mailboxIds">The people.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Each one's row (missing for one never seen).</returns>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyDictionary<System.Guid, PresenceRow>> GetPresenceAsync(System.Collections.Generic.IReadOnlyCollection<System.Guid> mailboxIds, System.Threading.CancellationToken ct = default);
}
