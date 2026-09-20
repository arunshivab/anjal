# QuotaPolicy

**Namespace:** `Anjal.Mailbox`

Hard quota enforcement at RCPT time. When the recipient resolves to a mailbox whose is at or above its , the recipient is refused with 452 4.2.2 Mailbox full - a temporary code, so the sending server queues and retries for a few days (RFC 5321 §4.5.4.1) while the owner makes room. Recipients that are not mailboxes (webhook targets, unknown addresses) are not affected; a quota of 0 means unlimited. Authenticated submissions are not checked here: the webmail refuses to compose when full, and a submitting client's own mailbox usage is not a reason to refuse delivery to someone else.

## Members

- **#ctor** *(method)* - Construct.
- **IsFull** *(method)* - Whether a mailbox is at or over its hard quota.
- **OnConnectAsync** *(method)* - _(no description)_
- **OnMailFromAsync** *(method)* - _(no description)_
- **OnRcptToAsync** *(method)* - _(no description)_
