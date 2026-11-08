# DeskItemRow

**Namespace:** `Anjal.Store`

rc.27 (DES-17 §2): a conversation on a shared desk that someone has acted on. Whether it is Open, Waiting or Not assigned follows from its mail (who wrote last); the row keeps only who has it and when it was marked done.

## Members

- **AssigneeId** *(property)* - The member who has it, or null.
- **CreatedAt** *(property)* - When the row was made.
- **DeskId** *(property)* - The desk (a shared mailbox).
- **DoneAt** *(property)* - When it was marked done; null while it is not.
- **Id** *(property)* - Its id.
- **LastReplyAt** *(property)* - When the desk last replied in it.
- **PendingSince** *(property)* - When the oldest mail the desk has not answered arrived; null when the desk wrote last.
- **RootMessageId** *(property)* - A mail of the conversation, to open it from elsewhere (Today).
- **ThreadKey** *(property)* - The conversation's keyed mark: never its subject.
- **UpdatedAt** *(property)* - When it last changed.
