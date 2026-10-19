# MailboxSink

**Namespace:** `Anjal.Mailbox`

that delivers accepted SMTP messages into tenant mailboxes. For each recipient: the domain is resolved to a tenant via tenant_domains, the local-part (with any +tag stripped) to a mailbox, the raw message is written to the mailbox's INBOX Maildir, and a metadata row is indexed in the store. Recipients with no matching mailbox are skipped so that another sink (e.g. the webhook router) can claim them. Folder choice: a message whose X-Anjal-Spam-Score header (written upstream by Anjal.Spam.SpamFilterSink) is at or above the tenant's is filed in instead of INBOX. A tenant sender rule overrides the score: allow forces INBOX, block forces Junk.

## Members

- **DiscardsKind** *(field)* - The mailbox document counting mail discarded from blocked senders (rc.22, item 32), by sender.
- **ForwardAudit** *(field)* - The audit action written for every forward (rc.22, item 29: forwards are logged).
- **ForwardedHeader** *(field)* - The header a forwarded copy carries (rc.22, item 29): each mailbox it passed, so a loop stops.
- **JunkFolder** *(field)* - Name of the folder spam is filed in.
- **MaxForwardHops** *(field)* - The most mailboxes a copy is forwarded through in a chain.
- **UnsubscribeLocal** *(field)* - The local part that takes unsubscribe requests for an organisation's applications (rc.22, item 51).
- **#ctor** *(method)* - Construct the sink.
- **AwayReplyAsync** *(method)* - rc.22 (item 30): the out-of-office reply. Once per sender while it is on; never to automatic mail, lists, no-reply addresses or the mailbox itself; outside the organisation only when the organisation and the person both allow it.
- **ChooseFolder** *(method)* - Decide the destination folder: a sender rule wins outright; otherwise the score is compared with the tenant threshold (a threshold of 0 or less disables junk filing for the tenant).
- **DeliverAsync** *(method)* - _(no description)_
- **DescribeServerRoleAddresses** *(method)* - One line for the start-up log: where postmaster@ and abuse@ at this server's own name are delivered (v1.0.0-rc.10, DEF-082). Built from the same rule the delivery uses, so the two cannot disagree.
- **EncodeHeader** *(method)* - A header value as RFC 2047 text when it is not plain ASCII.
- **ExpandAsync** *(method)* - Every mailbox an address reaches (rc.22, item 41): the mailbox itself, the mailbox an extra address delivers to, or each member of a group address. Empty when the address is not here. A group that takes mail only from its organisation gives no mailboxes to a sender outside it, and says so.
- **FileSentCopyAsync** *(method)* - File a copy of a message a mailbox itself sent into its Sent folder, already marked read - what the webmail does after sending, for mail that arrives through SMTP submission instead. Returns false when the address is not a local mailbox (a service account, for example).
- **FileSentCopyWithEvidenceAsync** *(method)* - , also linking the copy to the evidence of the submission as received (v1.0.0-rc.8), and returning the copy's id.
- **ForwardAsync** *(method)* - rc.22 (item 29, owner 18 Oct 2026): send a copy on. Always keeps the original; only by rule; outside the organisation only when its administrator allows it (individuals may forward anywhere); every forward is written to the audit trail. Automatic mail and bounces are never forwarded, and a copy stops after three mailboxes, so two rules cannot chase each other.
- **HasAttachment** *(method)* - Whether a parsed message carries an attachment: any part with a filename, or any non-text part inside a multipart body. A plain text or HTML message on its own does not count.
- **HeldNoticeAsync** *(method)* - rc.22 (item 39): the operator hears at once when mail is held because the scanner is down. The webmail repeats the notice every 30 minutes while anything is still held.
- **IsAutomaticMail** *(method)* - Whether a message is automatic - a list, a bounce, an auto-reply or any other machine mail - which is never answered automatically and never forwarded by a rule (RFC 3834).
- **IsRoleAddress** *(method)* - Whether a local part is one every mail domain must accept (postmaster, abuse).
- **IsRuleTarget** *(method)* - The category a mailbox's rules put this sender in, or null. An exact address beats a domain rule, the same precedence sender rules use.
- **IsTrustedAsync** *(method)* - Whether the mailbox trusts the sender although their mail arrives unencrypted.
- **ParseQuietly** *(method)* - Parse a message, or null when it cannot be parsed: the raw bytes are the truth and are stored anyway.
- **ResolveAsync** *(method)* - Resolve a recipient address to an enabled mailbox of an enabled tenant whose domain is registered. Returns when any link in that chain is missing.
- **ResolveExtraAsync** *(method)* - rc.22 (item 41): an extra address gives its mailbox; a group address gives its first member, so the address exists at RCPT TO and the webmail knows it is here. Delivery expands a group to every member with .
- **SendOnAsync** *(method)* - Deliver a message Anjal itself makes (a forwarded copy, an away reply, a notice): here when the recipient is a mailbox on this server, otherwise through the outgoing queue.
- **TrySplitAddress** *(method)* - Split an address into (local-part without "+tag", domain), both lowercased. Returns if the address has no "@" or an empty side.
- **WithReturnPath** *(method)* - The message as filed at final delivery (v1.0.0-rc.9, DEF-069; RFC 5321 4.4): a Return-Path line with the envelope sender first, after removing any Return-Path the sender included (the standard allows it; only the final server knows the real envelope - D-55). The evidence copy is untouched.
- **OperatorPostmaster** *(property)* - The operator's mailbox for postmaster@ this server; null for postmaster@ its parent domain.
- **Scanner** *(property)* - The virus scanner for files on mail from outside (rc.22, item 39); null when scanning is off.
- **ServerHostName** *(property)* - This server's own name, for postmaster@ it (v1.0.0-rc.8); null to not recognise it.
