# InactiveAccounts

**Namespace:** `Anjal.Mailbox`

rc.26 (item 63, POL-01 v1.1 §4, D-166; approved by the owner 26 Oct 2026): a mailbox nobody has signed in to for 180 days becomes inactive - incoming mail is refused, telling the sender to ask the person for another address - and 186 days after that it is deleted, with reminders before. Sign-ins through the webmail, a passkey, or a mail app on port 587 all count. Not counted: shared mailboxes, application inboxes, and a mailbox under legal hold (when a hold is lifted the clock starts again). These periods are Anjal policy; an organisation cannot change them.

## Members

- **DeletedAfter** *(field)* - Inactive for this long, it is deleted.
- **InactiveAfter** *(field)* - Without a sign-in for this long, a mailbox is inactive.
- **RefusalText** *(field)* - The reply to a sender (550 5.1.1).
- **ReminderDays** *(field)* - Reminders go this many days before deletion.
- **CountsAsync** *(method)* - Whether the inactive clock applies to a mailbox: a person's own, not shared, not under legal hold.
- **DeletedOn** *(method)* - When an inactive mailbox is deleted.
- **InactiveFrom** *(method)* - When a mailbox becomes inactive.
- **IsInactiveAsync** *(method)* - Whether a mailbox is inactive now.
