# SubmissionSink

**Namespace:** `Anjal.Server`

Delivery for mail submitted by an authenticated user on ports 587 and 465. Local recipients are delivered directly, exactly as inbound mail is; external recipients are queued for outbound delivery - DKIM-signed and retried by the outbound worker, the same path webmail sending uses; and a copy is filed in the sender's Sent folder when the sender is a mailbox. Before this, submitted mail went only to the inbound path, which knows local mailboxes and webhook rules and nothing else: an authenticated user could log in on 587 but could not reach any outside address (DEF-002).

## Members

- **#ctor** *(method)* - Construct.
- **DeliverAsync** *(method)* - _(no description)_
