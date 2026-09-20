# TenantStatePolicy

**Namespace:** `Anjal.Mailbox`

Defers mail for a disabled tenant instead of rejecting it. When a recipient resolves to a mailbox whose tenant is disabled, RCPT is answered with 450 4.2.1 Mailbox temporarily unavailable: the sending server keeps the message in its own queue and retries for its configured window (commonly four to five days) rather than bouncing immediately. Re-enabling the tenant inside that window loses nothing and generates no bounce; a tenant that is never re-enabled still bounces at the sender's ceiling. A disabled mailbox (as opposed to tenant) is not deferred here: that is a per-address decision the operator made, and the mailbox sink's permanent failure is the right answer.

## Members

- **#ctor** *(method)* - Construct.
- **OnConnectAsync** *(method)* - _(no description)_
- **OnMailFromAsync** *(method)* - _(no description)_
- **OnRcptToAsync** *(method)* - _(no description)_
