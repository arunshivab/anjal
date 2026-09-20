# IMessageStore

**Namespace:** `Anjal.Store`

Persistence interface for the Anjal mail server. Implementations include an in-memory store (for tests and demos) and a PostgreSQL store (for production). The interface is async-first because production code paths hit the network.

## Members

- **CountOutboundAsync** *(method)* - Count outbound messages by status. Used by /metrics for queue depth; a cheap indexed count.
- **CreateTagGrantAsync** *(method)* - Create a new tag grant for time-bounded per-case authorisation. Returns the saved grant with populated.
- **DeleteDkimKeyAsync** *(method)* - Remove the DKIM key for a sender domain.
- **DeleteLocalDomainAsync** *(method)* - Remove a local domain.
- **DeleteOutboundTlsPolicyAsync** *(method)* - Remove a policy for a destination domain.
- **DeleteRoutingRuleAsync** *(method)* - Remove the rule for a given local-part. Returns if a rule was deleted.
- **DeleteSmtpUserAsync** *(method)* - Remove an SMTP user by username.
- **EnqueueOutboundAsync** *(method)* - Enqueue an outbound message for delivery. Sets status to and assigns identifiers.
- **GetActiveTagGrantAsync** *(method)* - Look up an active (unexpired) grant by (local-part, tag). Returns if no matching grant exists or all matching grants have expired.
- **GetDkimKeyAsync** *(method)* - Look up the DKIM signing key for a sender domain.
- **GetInboundByIdAsync** *(method)* - Fetch a single inbound message by id. Returns if not found.
- **GetOutboundByIdAsync** *(method)* - Fetch a single outbound message by id. Returns if not found.
- **GetOutboundTlsPolicyAsync** *(method)* - Look up the TLS policy for a destination domain. Returns if no specific policy exists - the caller applies the configured default in that case.
- **GetRoutingRuleAsync** *(method)* - Look up the routing rule for a given local-part (case-insensitive). Returns if no rule exists.
- **GetSmtpUserAsync** *(method)* - Look up an SMTP user by username (case-insensitive).
- **IsLocalDomainAsync** *(method)* - Check whether a domain is local (case-insensitive).
- **LeaseOutboundBatchAsync** *(method)* - Lease up to messages whose next-attempt time has passed. Leased messages have their status flipped to so other workers won't pick them up. The worker must call after each attempt to release the lease (success, retry, or give up).
- **ListDkimKeysAsync** *(method)* - List all configured DKIM keys. The field IS populated; callers handling API responses should redact it.
- **ListLocalDomainsAsync** *(method)* - List all local domains.
- **ListOutboundTlsPoliciesAsync** *(method)* - List all configured TLS policies.
- **ListRoutingRulesAsync** *(method)* - List all routing rules in insertion order.
- **ListSmtpUsersAsync** *(method)* - List all configured SMTP users. Hashes ARE populated; callers handling API responses must redact the hash field.
- **MarkOutboundResultAsync** *(method)* - Record the result of an outbound send attempt. Updates status, increments the attempt counter, sets next-attempt-at for retries, and writes the last-error text.
- **SaveInboundMessageAsync** *(method)* - Persist an inbound message. Returns the saved message with populated.
- **SaveWebhookDeliveryAsync** *(method)* - Record a webhook delivery outcome for diagnostics.
- **UpsertDkimKeyAsync** *(method)* - Create or update a DKIM signing key for a sender domain. The (domain, selector) pair is unique - upserting with the same domain rotates the key or selector.
- **UpsertLocalDomainAsync** *(method)* - Register a domain as local. Idempotent: adding the same domain twice is not an error.
- **UpsertOutboundTlsPolicyAsync** *(method)* - Create or update a TLS policy for a destination domain. The domain is matched case-insensitively.
- **UpsertRoutingRuleAsync** *(method)* - Insert or replace a routing rule keyed by its local-part. Replacing preserves the existing identifier if a row with the same local-part already exists. Returns the saved rule with populated.
- **UpsertSmtpUserAsync** *(method)* - Create or update an SMTP submission user. Username is unique case-insensitively. The password should already be PBKDF2-hashed before calling this method - the store does not hash for you.
