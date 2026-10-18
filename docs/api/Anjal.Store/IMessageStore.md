# IMessageStore

**Namespace:** `Anjal.Store`

Persistence interface for the Anjal mail server. Implementations include an in-memory store (for tests and demos) and a PostgreSQL store (for production). The interface is async-first because production code paths hit the network.

## Members

- **AppendAuditAsync** *(method)* - Append an entry to the audit trail. Entries are never updated or deleted; in PostgreSQL a trigger refuses any attempt to.
- **CancelOutboundAsync** *(method)* - Stop a waiting outbound row of this sender: it is marked failed with "Cancelled by the sender". Only a row still waiting can be cancelled.
- **CompleteWebhookJobAsync** *(method)* - Record an attempt's outcome and schedule the next one if Pending.
- **CountOutboundAsync** *(method)* - Count outbound messages by status. Used by /metrics for queue depth; a cheap indexed count.
- **CountOutboundByRecipientDomainAsync** *(method)* - Outbound mail queued in a period, by the receiving domain and outcome (rc.15, item 61: acceptance by the big receivers).
- **CountOutboundBySenderAsync** *(method)* - Outbound mail queued in a period from given envelope senders, by outcome (rc.15, the applications box).
- **CountOutboundBySenderDomainAsync** *(method)* - Outbound mail queued in a period, by the sending domain and outcome (DES-11 D8: sudden rises and the 30-day activity chart).
- **CountWebhookJobsAsync** *(method)* - Count queued notifications by state, for metrics.
- **CreateTagGrantAsync** *(method)* - Create a new tag grant for time-bounded per-case authorisation. Returns the saved grant with populated.
- **DatabaseBytesAsync** *(method)* - The database's size on disk (DES-11 D8: its own part of the disk bar), or null when it cannot be measured (the in-memory store).
- **DeleteDkimKeyAsync** *(method)* - Remove the DKIM key for a sender domain.
- **DeleteLocalDomainAsync** *(method)* - Remove a local domain.
- **DeleteOutboundTlsPolicyAsync** *(method)* - Remove a policy for a destination domain.
- **DeleteRoutingRuleAsync** *(method)* - Remove the rule for a given local-part. Returns if a rule was deleted.
- **DeleteSettingAsync** *(method)* - Remove one setting.
- **DeleteSmtpUserAsync** *(method)* - Remove an SMTP user by username.
- **EnqueueOutboundAsync** *(method)* - Enqueue an outbound message for delivery. Sets status to and assigns identifiers.
- **EnqueueWebhookJobAsync** *(method)* - Queue a webhook notification. and are assigned.
- **GetActiveTagGrantAsync** *(method)* - Look up an active (unexpired) grant by (local-part, tag). Returns if no matching grant exists or all matching grants have expired.
- **GetDkimKeyAsync** *(method)* - Look up the DKIM signing key for a sender domain.
- **GetInboundByIdAsync** *(method)* - Fetch a single inbound message by id. Returns if not found.
- **GetOutboundByIdAsync** *(method)* - Fetch a single outbound message by id. Returns if not found.
- **GetOutboundTlsPolicyAsync** *(method)* - Look up the TLS policy for a destination domain. Returns if no specific policy exists - the caller applies the configured default in that case.
- **GetRoutingRuleAsync** *(method)* - Look up the routing rule for a given local-part (case-insensitive). Returns if no rule exists.
- **GetServiceRecordAsync** *(method)* - A record the whole service keeps (rc.15, item 61): the operator's daily disk record, restore drills and the mail server's refusal counts, as JSON. Null when none is kept yet.
- **GetSmtpUserAsync** *(method)* - Look up an SMTP user by username (case-insensitive).
- **ImportSettingsAsync** *(method)* - Insert the settings that are not stored yet; never overwrite one that is.
- **IsLocalDomainAsync** *(method)* - Check whether a domain is local (case-insensitive).
- **LeaseOutboundBatchAsync** *(method)* - Lease up to messages whose next-attempt time has passed. Leased messages have their status flipped to so other workers won't pick them up. The worker must call after each attempt to release the lease (success, retry, or give up).
- **LeaseWebhookJobsAsync** *(method)* - Lease due notifications, including any whose previous lease lapsed (a worker stopped mid-attempt). Leased jobs move to Sending.
- **ListAuditAsync** *(method)* - The most recent audit entries, newest first.
- **ListDkimKeysAsync** *(method)* - List all configured DKIM keys. The field IS populated; callers handling API responses should redact it.
- **ListGreylistAsync** *(method)* - All remembered greylisting triplets.
- **ListLocalDomainsAsync** *(method)* - List all local domains.
- **ListOutboundBySendersAsync** *(method)* - rc.17 (Sending systems, design review [7b]): outbound messages from any of these envelope senders created in a period, newest first. Each row's holds only the first 8 KB of the message (enough for its headers).
- **ListOutboundForSenderAsync** *(method)* - The Outbox (rc.12, item 9): one sender's outbound rows still waiting or being sent, oldest first.
- **ListOutboundTlsPoliciesAsync** *(method)* - List all configured TLS policies.
- **ListRoutingRulesAsync** *(method)* - List all routing rules in insertion order.
- **ListSettingsAsync** *(method)* - The stored settings of one scope, by key.
- **ListSmtpUsersAsync** *(method)* - List all configured SMTP users. Hashes ARE populated; callers handling API responses must redact the hash field.
- **ListWebhookDeliveriesAsync** *(method)* - rc.17 (design review [7d]): the recorded attempts to post these inbound messages to their webhooks, newest first.
- **ListWebhookJobsAsync** *(method)* - rc.17 (design review [7d]): notifications to addresses at these domains created since a time, newest first.
- **MarkOutboundResultAsync** *(method)* - Record the result of an outbound send attempt. Updates status, increments the attempt counter, sets next-attempt-at for retries, and writes the last-error text.
- **OldestPendingOutboundAsync** *(method)* - When the oldest message still waiting in the outbound queue was queued (rc.14); null when none waits.
- **RemoveOutboundContentAsync** *(method)* - v1.0.0-rc.21.1 (DEF-117, owner 16 Oct 2026, option (a)): remove the content of delivered or given-up messages that ended before a time, keeping only their header block, so the queue stops holding every message for ever while the record of who, when and what happened stays. Messages still being tried are never touched; the evidence store is not touched.
- **ReplaceGreylistAsync** *(method)* - Replace the remembered triplets with , in one transaction.
- **ReplayWebhookJobAsync** *(method)* - rc.17 (design review [7d], Replay): post a notification again - it waits to be sent now, with its tries counted afresh and a new give-up time. Returns false when it is not there or is being sent at this moment.
- **RetryOutboundNowAsync** *(method)* - Make a waiting outbound row of this sender due now ("Try now").
- **SaveInboundMessageAsync** *(method)* - Persist an inbound message. Returns the saved message with populated.
- **SaveWebhookDeliveryAsync** *(method)* - Record a webhook delivery outcome for diagnostics.
- **SetServiceRecordAsync** *(method)* - Keep a service record (rc.15, item 61), replacing the one of the same kind.
- **UpsertDkimKeyAsync** *(method)* - Create or update a DKIM signing key for a sender domain. The (domain, selector) pair is unique - upserting with the same domain rotates the key or selector.
- **UpsertLocalDomainAsync** *(method)* - Register a domain as local. Idempotent: adding the same domain twice is not an error.
- **UpsertOutboundTlsPolicyAsync** *(method)* - Create or update a TLS policy for a destination domain. The domain is matched case-insensitively.
- **UpsertRoutingRuleAsync** *(method)* - Insert or replace a routing rule keyed by its local-part. Replacing preserves the existing identifier if a row with the same local-part already exists. Returns the saved rule with populated.
- **UpsertSettingAsync** *(method)* - Insert or replace one setting.
- **UpsertSmtpUserAsync** *(method)* - Create or update an SMTP submission user. Username is unique case-insensitively. The password should already be PBKDF2-hashed before calling this method - the store does not hash for you.
- **VerifyAuditChainAsync** *(method)* - Check the audit trail's chain from the first chained entry to the last (rc.13): each entry's chain must follow from the one before it.
