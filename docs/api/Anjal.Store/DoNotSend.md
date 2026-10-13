# DoNotSend

**Namespace:** `Anjal.Store`

rc.17 (design review [7d], owner 11 Oct 2026): the do-not-send list. Addresses that bounced hard, were given up on three times, or that an administrator added, are not sent to again by the organisation's applications (the addresses in its "apps" document): the queue marks such a message "Suppressed" and /api/outbound answers "suppressed". People's own mail is never held by it.

## Members

- **AppsKind** *(field)* - The tenant document that holds the organisation's applications (written by the webmail).
- **ByAnjal** *(field)* - Who adds an entry when the server does it.
- **Kind** *(field)* - The tenant document that holds the list.
- **SuppressedPrefix** *(field)* - The start of LastError on a message the list held back.
- **AppSendersAsync** *(method)* - The addresses an organisation's applications send as.
- **CheckAsync** *(method)* - Why a message from an application to an address must not be sent, or null to send it.
- **HardReason** *(method)* - The reason a permanent refusal means the address is gone for good, or null when it is some other refusal (a policy, a full mailbox, a message too big) that a later message might pass.
- **NoteFailureAsync** *(method)* - After an application's message failed for good: a hard bounce (the mailbox or the domain does not exist) adds the address at once; giving up adds it the third time.
- **ReadAsync** *(method)* - Read an organisation's list.
- **SenderAsync** *(method)* - The organisation an envelope sender belongs to, and whether it is one of its applications.
- **WriteAsync** *(method)* - Save an organisation's list.
