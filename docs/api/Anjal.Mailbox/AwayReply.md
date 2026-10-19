# AwayReply

**Namespace:** `Anjal.Mailbox`

A person's out-of-office reply (rc.22, item 30), kept as their "away" document. Each sender is answered once while it is on; mail from applications, lists and no-reply addresses never is.

## Members

- **Kind** *(field)* - The mailbox document kind.
- **ReplyToChoices** *(field)* - The choices for who is answered: everyone, only the organisation, only contacts.
- **IsActive** *(method)* - Whether the reply is on at a time: switched on, after From, before Until.
- **ReadAsync** *(method)* - Read a mailbox's away reply (off when it has none).
- **Skips** *(method)* - Whether a sender is never answered: no address, the mailbox itself, a list or automatic message, a no-reply or bounce address, or a word from .
- **WriteAsync** *(method)* - Save a mailbox's away reply.
- **From** *(property)* - From when (null: from now).
- **On** *(property)* - Whether the reply is on.
- **Replied** *(property)* - Who was answered and when, so each sender hears once.
- **ReplyTo** *(property)* - "everyone", "org" (only people in the organisation) or "contacts" (only saved contacts).
- **Skip** *(property)* - Addresses or words never answered, comma separated (for example "newsletters, no-reply@").
- **Subject** *(property)* - The reply's subject; empty gives "Automatic reply: " and the original subject.
- **Text** *(property)* - The reply's text.
- **Until** *(property)* - Until when (null: until it is turned off); it turns itself off then.
