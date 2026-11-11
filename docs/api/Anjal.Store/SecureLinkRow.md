# SecureLinkRow

**Namespace:** `Anjal.Store`

rc.28 (DES-19, item 74): one secure link - a mail kept on Anjal for one outside person, sealed with a key made from the link's token (which Anjal does not keep), opened with a code mailed to that person.

## Members

- **IsOpenAt** *(method)* - True while it can be opened at .
- **CreatedAt** *(property)* - When it was made.
- **ExpiresAt** *(property)* - When it ends by itself (7 days after it was made).
- **Id** *(property)* - Its id.
- **LastCodeAt** *(property)* - When a code was last mailed.
- **LockedAt** *(property)* - When too many wrong codes stopped it; null while it stands.
- **MailboxId** *(property)* - The sender's mailbox.
- **MailedAt** *(property)* - When the notice mail went.
- **MessageId** *(property)* - The sent mail's Message-ID, with its angle brackets.
- **OpenedAt** *(property)* - When the mail was first shown after a right code.
- **PurgedAt** *(property)* - When its sealed copy was removed (after it ended); null while kept.
- **Recipient** *(property)* - The outside address it was made for.
- **RepliedAt** *(property)* - When the outside person last replied.
- **Replies** *(property)* - Replies sent through it.
- **Sealed** *(property)* - The mail as sent, sealed under the token's key (AES-256-GCM).
- **SentBy** *(property)* - The person who signed in and sent it (the mailbox for a person; the member for a desk).
- **SentCopyId** *(property)* - The sender's Sent copy, when one was kept.
- **StoppedAt** *(property)* - When the sender stopped it; null while it stands.
- **TokenSha256** *(property)* - The SHA-256 of the link's token, hex; the token itself is never kept.
- **WrongCodes** *(property)* - Wrong codes typed, in all.
