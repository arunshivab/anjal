# IChatStore

**Namespace:** `Anjal.Store`

rc.28 (DES-21): what chat and online status need from the store.

## Members

- **AddChatMemberAsync** *(method)* - Add a person to a group (again, when they had left).
- **AddChatMessageAsync** *(method)* - Keep a message; the chat moves to the top of its members' lists.
- **AddChatRoomAsync** *(method)* - Make a chat with its first members.
- **CountUnreadChatsAsync** *(method)* - How many of a person's chats have messages from others they have not read.
- **FindDirectChatAsync** *(method)* - The one-to-one chat of two people, if there is one.
- **GetChatRoomAsync** *(method)* - One chat.
- **GetPresenceAsync** *(method)* - People's statuses.
- **LeaveChatAsync** *(method)* - A person leaves a group.
- **ListChatMembersAsync** *(method)* - A chat's members, those who left included.
- **ListChatMessagesAsync** *(method)* - A chat's messages written after a time (oldest first), at most of the latest.
- **ListChatRoomsAsync** *(method)* - A person's chats, the latest first.
- **MarkChatReadAsync** *(method)* - A person has read a chat up to a time.
- **RemoveChatMessageAsync** *(method)* - The writer removes a message written after ; its words are deleted.
- **SetPresenceAsync** *(method)* - The person chose a status and a line.
- **TouchPresenceAsync** *(method)* - A page of Anjal was used now.
