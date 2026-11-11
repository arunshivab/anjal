namespace Anjal.Store;

/// <summary>rc.28 additions to the in-memory store: chat and online status (DES-21).</summary>
public sealed partial class InMemoryMessageStore : IChatStore
{
    private readonly List<ChatRoomRow> chatRooms = new();
    private readonly List<ChatMemberRow> chatMembers = new();
    private readonly List<ChatMessageRow> chatMessages = new();
    private readonly Dictionary<System.Guid, PresenceRow> presence = new();

    /// <inheritdoc/>
    public Task<ChatRoomRow> AddChatRoomAsync(ChatRoomRow room, IReadOnlyCollection<System.Guid> members, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(room);
        System.ArgumentNullException.ThrowIfNull(members);
        lock (this.gate)
        {
            ChatRoomRow copy = CloneRoom(room);
            if (copy.Id == System.Guid.Empty)
            {
                copy.Id = System.Guid.NewGuid();
            }
            copy.LastMessageAt = copy.CreatedAt;
            this.chatRooms.Add(copy);
            foreach (System.Guid m in members.Distinct())
            {
                this.chatMembers.Add(new ChatMemberRow { RoomId = copy.Id, MailboxId = m, JoinedAt = copy.CreatedAt, LastReadAt = copy.CreatedAt });
            }
            return Task.FromResult(CloneRoom(copy));
        }
    }

    /// <inheritdoc/>
    public Task<ChatRoomRow?> GetChatRoomAsync(System.Guid id, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            ChatRoomRow? r = this.chatRooms.FirstOrDefault(x => x.Id == id);
            return Task.FromResult(r is null ? null : CloneRoom(r));
        }
    }

    /// <inheritdoc/>
    public Task<ChatRoomRow?> FindDirectChatAsync(System.Guid a, System.Guid b, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            ChatRoomRow? r = this.chatRooms.Where(x => !x.IsGroup).FirstOrDefault(x =>
            {
                var ids = this.chatMembers.Where(m => m.RoomId == x.Id).Select(m => m.MailboxId).ToHashSet();
                return ids.Count == (a == b ? 1 : 2) && ids.Contains(a) && ids.Contains(b);
            });
            return Task.FromResult(r is null ? null : CloneRoom(r));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ChatRoomRow>> ListChatRoomsAsync(System.Guid mailboxId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            var mine = this.chatMembers.Where(m => m.MailboxId == mailboxId && m.LeftAt is null).Select(m => m.RoomId).ToHashSet();
            IReadOnlyList<ChatRoomRow> list = this.chatRooms.Where(r => mine.Contains(r.Id)).OrderByDescending(r => r.LastMessageAt).Select(CloneRoom).ToList();
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ChatMemberRow>> ListChatMembersAsync(System.Guid roomId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<ChatMemberRow> list = this.chatMembers.Where(m => m.RoomId == roomId)
                .Select(m => new ChatMemberRow { RoomId = m.RoomId, MailboxId = m.MailboxId, JoinedAt = m.JoinedAt, LeftAt = m.LeftAt, LastReadAt = m.LastReadAt }).ToList();
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc/>
    public Task AddChatMemberAsync(System.Guid roomId, System.Guid mailboxId, System.DateTimeOffset at, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            if (this.chatMembers.FirstOrDefault(m => m.RoomId == roomId && m.MailboxId == mailboxId) is ChatMemberRow was)
            {
                if (was.LeftAt is not null)
                {
                    was.LeftAt = null;
                    was.JoinedAt = at;
                    was.LastReadAt = at;
                }
            }
            else
            {
                this.chatMembers.Add(new ChatMemberRow { RoomId = roomId, MailboxId = mailboxId, JoinedAt = at, LastReadAt = at });
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task LeaveChatAsync(System.Guid roomId, System.Guid mailboxId, System.DateTimeOffset at, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            if (this.chatMembers.FirstOrDefault(m => m.RoomId == roomId && m.MailboxId == mailboxId && m.LeftAt is null) is ChatMemberRow m2)
            {
                m2.LeftAt = at;
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<ChatMessageRow> AddChatMessageAsync(ChatMessageRow message, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(message);
        lock (this.gate)
        {
            var copy = new ChatMessageRow
            {
                Id = message.Id == System.Guid.Empty ? System.Guid.NewGuid() : message.Id,
                RoomId = message.RoomId,
                SenderId = message.SenderId,
                Body = message.Body,
                CreatedAt = message.CreatedAt,
            };
            this.chatMessages.Add(copy);
            if (this.chatRooms.FirstOrDefault(r => r.Id == message.RoomId) is ChatRoomRow room && room.LastMessageAt < message.CreatedAt)
            {
                room.LastMessageAt = message.CreatedAt;
            }
            return Task.FromResult(CloneChat(copy));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ChatMessageRow>> ListChatMessagesAsync(System.Guid roomId, System.DateTimeOffset after, int limit, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<ChatMessageRow> list = this.chatMessages.Where(m => m.RoomId == roomId && m.CreatedAt > after)
                .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Take(limit).Reverse().Select(CloneChat).ToList();
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc/>
    public Task<bool> RemoveChatMessageAsync(System.Guid messageId, System.Guid senderId, System.DateTimeOffset at, System.DateTimeOffset notBefore, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            if (this.chatMessages.FirstOrDefault(m => m.Id == messageId && m.SenderId == senderId && m.RemovedAt is null && m.CreatedAt >= notBefore) is not ChatMessageRow m)
            {
                return Task.FromResult(false);
            }
            m.RemovedAt = at;
            m.Body = string.Empty;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc/>
    public Task MarkChatReadAsync(System.Guid roomId, System.Guid mailboxId, System.DateTimeOffset at, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            if (this.chatMembers.FirstOrDefault(m => m.RoomId == roomId && m.MailboxId == mailboxId) is ChatMemberRow m && m.LastReadAt < at)
            {
                m.LastReadAt = at;
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<int> CountUnreadChatsAsync(System.Guid mailboxId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            int n = this.chatMembers.Where(m => m.MailboxId == mailboxId && m.LeftAt is null)
                .Count(m => this.chatMessages.Any(x => x.RoomId == m.RoomId && x.SenderId != mailboxId && x.CreatedAt > m.LastReadAt && x.RemovedAt is null));
            return Task.FromResult(n);
        }
    }

    /// <inheritdoc/>
    public Task TouchPresenceAsync(System.Guid mailboxId, System.DateTimeOffset at, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            if (!this.presence.TryGetValue(mailboxId, out PresenceRow? p))
            {
                p = new PresenceRow { MailboxId = mailboxId };
                this.presence[mailboxId] = p;
            }
            p.LastSeenAt = at;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SetPresenceAsync(System.Guid mailboxId, string status, string note, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(status);
        System.ArgumentNullException.ThrowIfNull(note);
        lock (this.gate)
        {
            if (!this.presence.TryGetValue(mailboxId, out PresenceRow? p))
            {
                p = new PresenceRow { MailboxId = mailboxId };
                this.presence[mailboxId] = p;
            }
            p.Status = status;
            p.Note = note;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<System.Guid, PresenceRow>> GetPresenceAsync(IReadOnlyCollection<System.Guid> mailboxIds, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(mailboxIds);
        lock (this.gate)
        {
            IReadOnlyDictionary<System.Guid, PresenceRow> found = mailboxIds.Distinct().Where(this.presence.ContainsKey)
                .ToDictionary(id => id, id => new PresenceRow { MailboxId = id, LastSeenAt = this.presence[id].LastSeenAt, Status = this.presence[id].Status, Note = this.presence[id].Note });
            return Task.FromResult(found);
        }
    }

    private static ChatRoomRow CloneRoom(ChatRoomRow r) => new()
    {
        Id = r.Id,
        TenantId = r.TenantId,
        Name = r.Name,
        IsGroup = r.IsGroup,
        CreatedBy = r.CreatedBy,
        CreatedAt = r.CreatedAt,
        LastMessageAt = r.LastMessageAt,
    };

    private static ChatMessageRow CloneChat(ChatMessageRow m) => new()
    {
        Id = m.Id,
        RoomId = m.RoomId,
        SenderId = m.SenderId,
        Body = m.Body,
        CreatedAt = m.CreatedAt,
        RemovedAt = m.RemovedAt,
    };
}
