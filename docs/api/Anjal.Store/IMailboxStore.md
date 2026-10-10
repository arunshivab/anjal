# IMailboxStore

**Namespace:** `Anjal.Store`

Persistence interface for multi-tenant mailbox storage: tenants, their domains, mailboxes, folders and per-message metadata. Message bodies are NOT stored here - they live in Maildir files on disk; this interface holds only the index. Both and implement it alongside .

## Members

- **AddMailboxUsageAsync** *(method)* - Adjust by a signed delta and return the new total. Returns if no such mailbox.
- **AddTrustedSenderAsync** *(method)* - Trust an address although its mail arrives unencrypted; adding it twice is harmless.
- **CountArrivalsByHourAsync** *(method)* - How many messages arrived in every mailbox, hour by hour, since a moment (rc.14, the Anjal console's service health). Hours with none are left out.
- **CountMessagesAsync** *(method)* - Count messages in a folder (or the whole mailbox when is null).
- **CountReceivedByTenantAsync** *(method)* - Messages received (Junk included) per organisation in a period (rc.15, the operator's dashboard). Organisations with none are left out.
- **CountSearchAsync** *(method)* - Count of matches.
- **CountUnreadAsync** *(method)* - Count unread (not seen) messages in a folder, or the whole mailbox when is null.
- **DeleteCategoryAsync** *(method)* - Delete a category and clear it from any message carrying it.
- **DeleteCategoryRuleAsync** *(method)* - Remove a sender-to-category rule.
- **DeleteEmptyFolderAsync** *(method)* - Remove one of a mailbox's own folders, only when it holds no messages (rc.12, item 24): nothing is ever deleted with a folder.
- **DeleteMailboxAsync** *(method)* - Remove a mailbox and, by cascade, its folders and message rows. Maildir files on disk are NOT removed.
- **DeleteMailboxSenderRuleAsync** *(method)* - Delete a personal sender rule. Matches on both the mailbox and the rule id, so a rule can only ever be removed by the mailbox that owns it.
- **DeleteMessageAsync** *(method)* - Remove a message's index row. Returns if a row was removed. The caller is responsible for the Maildir file.
- **DeleteSenderRuleAsync** *(method)* - Remove a sender rule. Returns if a row was removed.
- **DeleteTenantAsync** *(method)* - Remove a tenant and, by cascade, its domains, mailboxes, folders and message rows. Maildir files on disk are NOT removed.
- **DeleteTenantDomainAsync** *(method)* - Remove a domain. Returns if a row was removed.
- **EnsureFolderAsync** *(method)* - Ensure a folder exists for a mailbox. Idempotent by (mailbox, name); returns the existing or newly created row.
- **GetActivityAsync** *(method)* - Everything the dashboard shows for one mailbox over a period, computed in the store rather than by reading every message.
- **GetCategoryAsync** *(method)* - One category by id, or null.
- **GetMailFiguresAsync** *(method)* - A dashboard's figures for one period (rc.15): received, sent and Junk; both step by step through the period in the given zone; and checked mail by spam score. Totals only.
- **GetMailboxAsync** *(method)* - Look up a mailbox by local-part and domain (case-insensitive). Null if none.
- **GetMailboxByIdAsync** *(method)* - Look up a mailbox by id. Null if none.
- **GetMailboxDocumentAsync** *(method)* - Read one of a mailbox's own documents (rc.12): its contacts, contact groups, templates or rules, each kept as one JSON document per kind. Returns null when the mailbox has none of that kind yet.
- **GetMessageByIdAsync** *(method)* - Fetch one message row by id. Null if none.
- **GetSignatureAsync** *(method)* - A mailbox's signature, formatted and plain. Kept apart from the general mailbox update so that an administrator changing, say, a quota cannot wipe it. Both empty when none is set.
- **GetTenantAsync** *(method)* - Look up a tenant by slug (case-insensitive). Null if none.
- **GetTenantByIdAsync** *(method)* - Look up a tenant by id. Null if none.
- **GetTenantDocumentAsync** *(method)* - Read one of an organisation's own documents (rc.13): its sign-in look, policies and defaults, each kept as one JSON document per kind. Returns null when the organisation has none of that kind yet.
- **GetTenantDomainAsync** *(method)* - Look up a domain row (case-insensitive). Null if none.
- **ListCategoriesAsync** *(method)* - Tenant defaults plus one mailbox's own, shared first then by slot.
- **ListCategoryRulesAsync** *(method)* - A mailbox's sender-to-category rules.
- **ListFoldersAsync** *(method)* - List a mailbox's folders ordered by name, INBOX first.
- **ListMailboxSenderRulesAsync** *(method)* - A mailbox's personal sender rules, by pattern.
- **ListMailboxesAsync** *(method)* - List mailboxes, optionally restricted to one tenant. Ordered by domain then local-part.
- **ListMessageNamesAsync** *(method)* - Each message's sender and recipients only, for every message in a folder (owner, 9 Oct 2026): enough to put them in order by name, which needs the names decoded first.
- **ListMessagesAsync** *(method)* - Page through a folder's messages, newest first.
- **ListMessagesByIdsAsync** *(method)* - The messages of a mailbox with these ids, in no particular order (one page of a list put in order by name).
- **ListMessagesBySeenAsync** *(method)* - Page through a folder's read messages, or its unread ones, newest first (rc.11, item 7: the All, Unread and Read filter).
- **ListMessagesSortedAsync** *(method)* - Page through a folder's messages in a chosen order (owner, 9 Oct 2026): "oldest" first, by "subject" A to Z (a leading Re: or Fwd: aside), "size" biggest first, "unread" first or with "attachments" first; anything else is newest first. Ties are newest first.
- **ListSenderRulesAsync** *(method)* - List a tenant's sender rules ordered by pattern.
- **ListTenantDomainsAsync** *(method)* - List domains, optionally restricted to one tenant. Ordered by domain.
- **ListTenantsAsync** *(method)* - List all tenants ordered by slug.
- **ListTrustedSendersAsync** *(method)* - Addresses the mailbox trusts although their mail arrives unencrypted (v1.0.0-rc.7).
- **MoveMessageAsync** *(method)* - Move a message to another folder of the same mailbox, recording the new Maildir file path. Returns the updated row, or if no such message.
- **RemoveTrustedSenderAsync** *(method)* - Stop trusting an address.
- **SaveMessageAsync** *(method)* - Record a delivered message. Returns the saved row with and populated.
- **SearchMessagesAsync** *(method)* - Case-insensitive substring search over subject, From, To and envelope sender, newest first. null searches every folder.
- **SetMailboxDocumentAsync** *(method)* - Create or replace one of a mailbox's own documents (rc.12).
- **SetMailboxPreferencesAsync** *(method)* - Save a mailbox's own preferences (rc.11): time zone, language, density, layout, folded rail, date format and week start. Only this call changes them; never does. Values are checked first, and anything unknown is saved as its default.
- **SetMessageCategoryAsync** *(method)* - Put a category on a message, or clear it with null.
- **SetMessageFlagsAsync** *(method)* - Update a message's flags and, optionally, the Maildir file name that now carries them (Maildir encodes flags in the file name, so a flag change on disk is a rename). Returns the updated row, or if no such message.
- **SetSignatureAsync** *(method)* - Set a mailbox's signature (already sanitised by the caller).
- **SetTenantDocumentAsync** *(method)* - Create or replace one of an organisation's own documents (rc.13).
- **SumFolderBytesAsync** *(method)* - The space each of a mailbox's folders takes, in bytes (rc.15, item 58). Empty folders are left out.
- **UpsertCategoryAsync** *(method)* - Create or rename a category. A row with null is a tenant default; otherwise it belongs to that mailbox. The slot is assigned by the caller and never recomputed.
- **UpsertCategoryRuleAsync** *(method)* - Create or replace a sender-to-category rule for a mailbox.
- **UpsertMailboxAsync** *(method)* - Create or update a mailbox keyed by (local-part, domain). On update the password hash is replaced only when the supplied hash is non-empty; is never overwritten.
- **UpsertMailboxSenderRuleAsync** *(method)* - Create or update a personal sender rule for one mailbox. Personal rules come from that mailbox's own Report spam and Not spam, and affect no other mailbox.
- **UpsertSenderRuleAsync** *(method)* - Create or replace a sender rule keyed by (tenant, pattern). Returns the saved row.
- **UpsertTenantAsync** *(method)* - Create or update a tenant keyed by . Returns the saved row with populated.
- **UpsertTenantDomainAsync** *(method)* - Register a domain for a tenant. The domain is unique across the deployment; upserting an existing domain moves it to the given tenant and updates the verified flag.
