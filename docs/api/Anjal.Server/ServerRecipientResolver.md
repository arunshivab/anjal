# ServerRecipientResolver

**Namespace:** `Anjal.Server`

Whether this server has somewhere to put mail for an address on one of its own domains: a mailbox, or a routing rule that forwards it to SIGMA, Lipi or another webhook. Used to refuse an unknown recipient at RCPT TO, before a megabyte of attachments is transferred for a typo (DEF-042). It only ever answers false when it is certain. A store that cannot be reached, or any other doubt, returns null: the recipient is accepted and the decision is made at delivery, where a temporary failure defers rather than bounces (DEF-003).

## Members

- **#ctor** *(method)* - Construct.
- **ExistsAsync** *(method)* - _(no description)_
