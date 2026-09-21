# MailboxSink

**Namespace:** `Anjal.Mailbox`

that delivers accepted SMTP messages into tenant mailboxes. For each recipient: the domain is resolved to a tenant via tenant_domains, the local-part (with any +tag stripped) to a mailbox, the raw message is written to the mailbox's INBOX Maildir, and a metadata row is indexed in the store. Recipients with no matching mailbox are skipped so that another sink (e.g. the webhook router) can claim them. Folder choice: a message whose X-Anjal-Spam-Score header (written upstream by Anjal.Spam.SpamFilterSink) is at or above the tenant's is filed in instead of INBOX. A tenant sender rule overrides the score: allow forces INBOX, block forces Junk.

## Members

- **JunkFolder** *(field)* - Name of the folder spam is filed in.
- **#ctor** *(method)* - Construct the sink.
- **CategoryForAsync** *(method)* - The category a mailbox's rules put this sender in, or null. An exact address beats a domain rule, the same precedence sender rules use.
- **ChooseFolder** *(method)* - Decide the destination folder: a sender rule wins outright; otherwise the score is compared with the tenant threshold (a threshold of 0 or less disables junk filing for the tenant).
- **DeliverAsync** *(method)* - _(no description)_
- **FileSentCopyAsync** *(method)* - File a copy of a message a mailbox itself sent into its Sent folder, already marked read - what the webmail does after sending, for mail that arrives through SMTP submission instead. Returns false when the address is not a local mailbox (a service account, for example).
- **HasAttachment** *(method)* - Whether a parsed message carries an attachment: any part with a filename, or any non-text part inside a multipart body. A plain text or HTML message on its own does not count.
- **ResolveAsync** *(method)* - Resolve a recipient address to an enabled mailbox of an enabled tenant whose domain is registered. Returns when any link in that chain is missing.
- **TrySplitAddress** *(method)* - Split an address into (local-part without "+tag", domain), both lowercased. Returns if the address has no "@" or an empty side.
