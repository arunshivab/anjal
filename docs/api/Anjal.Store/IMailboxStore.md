# IMailboxStore

**Namespace:** `Anjal.Store`

Persistence interface for multi-tenant mailbox storage: tenants, their domains, mailboxes, folders and per-message metadata. Message bodies are NOT stored here - they live in Maildir files on disk; this interface holds only the index. Both and implement it alongside .

## Members

- **AddMailboxUsageAsync** *(method)* - Adjust by a signed delta and return the new total. Returns if no such mailbox.
- **CountMessagesAsync** *(method)* - Count messages in a folder (or the whole mailbox when is null).
- **DeleteMailboxAsync** *(method)* - Remove a mailbox and, by cascade, its folders and message rows. Maildir files on disk are NOT removed.
- **DeleteMessageAsync** *(method)* - Remove a message's index row. Returns if a row was removed. The caller is responsible for the Maildir file.
- **DeleteSenderRuleAsync** *(method)* - Remove a sender rule. Returns if a row was removed.
- **DeleteTenantAsync** *(method)* - Remove a tenant and, by cascade, its domains, mailboxes, folders and message rows. Maildir files on disk are NOT removed.
- **DeleteTenantDomainAsync** *(method)* - Remove a domain. Returns if a row was removed.
- **EnsureFolderAsync** *(method)* - Ensure a folder exists for a mailbox. Idempotent by (mailbox, name); returns the existing or newly created row.
- **GetMailboxAsync** *(method)* - Look up a mailbox by local-part and domain (case-insensitive). Null if none.
- **GetMailboxByIdAsync** *(method)* - Look up a mailbox by id. Null if none.
- **GetMessageByIdAsync** *(method)* - Fetch one message row by id. Null if none.
- **GetTenantAsync** *(method)* - Look up a tenant by slug (case-insensitive). Null if none.
- **GetTenantByIdAsync** *(method)* - Look up a tenant by id. Null if none.
- **GetTenantDomainAsync** *(method)* - Look up a domain row (case-insensitive). Null if none.
- **ListFoldersAsync** *(method)* - List a mailbox's folders ordered by name, INBOX first.
- **ListMailboxesAsync** *(method)* - List mailboxes, optionally restricted to one tenant. Ordered by domain then local-part.
- **ListMessagesAsync** *(method)* - Page through a folder's messages, newest first.
- **ListSenderRulesAsync** *(method)* - List a tenant's sender rules ordered by pattern.
- **ListTenantDomainsAsync** *(method)* - List domains, optionally restricted to one tenant. Ordered by domain.
- **ListTenantsAsync** *(method)* - List all tenants ordered by slug.
- **MoveMessageAsync** *(method)* - Move a message to another folder of the same mailbox, recording the new Maildir file path. Returns the updated row, or if no such message.
- **SaveMessageAsync** *(method)* - Record a delivered message. Returns the saved row with and populated.
- **SetMessageFlagsAsync** *(method)* - Update a message's flags and, optionally, the Maildir file name that now carries them (Maildir encodes flags in the file name, so a flag change on disk is a rename). Returns the updated row, or if no such message.
- **UpsertMailboxAsync** *(method)* - Create or update a mailbox keyed by (local-part, domain). On update the password hash is replaced only when the supplied hash is non-empty; is never overwritten.
- **UpsertSenderRuleAsync** *(method)* - Create or replace a sender rule keyed by (tenant, pattern). Returns the saved row.
- **UpsertTenantAsync** *(method)* - Create or update a tenant keyed by . Returns the saved row with populated.
- **UpsertTenantDomainAsync** *(method)* - Register a domain for a tenant. The domain is unique across the deployment; upserting an existing domain moves it to the given tenant and updates the verified flag.
