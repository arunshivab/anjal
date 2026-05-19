# IMessageStore

**Namespace:** `Anjal.Store`

Persistence interface for the Anjal mail server. Implementations include an in-memory store (for tests and demos) and a PostgreSQL store (for production). The interface is async-first because production code paths hit the network.

## Members

- **CreateTagGrantAsync** *(method)* - Create a new tag grant for time-bounded per-case authorisation. Returns the saved grant with populated.
- **DeleteRoutingRuleAsync** *(method)* - Remove the rule for a given local-part. Returns if a rule was deleted.
- **GetActiveTagGrantAsync** *(method)* - Look up an active (unexpired) grant by (local-part, tag). Returns if no matching grant exists or all matching grants have expired.
- **GetRoutingRuleAsync** *(method)* - Look up the routing rule for a given local-part (case-insensitive). Returns if no rule exists.
- **ListRoutingRulesAsync** *(method)* - List all routing rules in insertion order.
- **SaveInboundMessageAsync** *(method)* - Persist an inbound message. Returns the saved message with populated.
- **SaveWebhookDeliveryAsync** *(method)* - Record a webhook delivery outcome for diagnostics.
- **UpsertRoutingRuleAsync** *(method)* - Insert or replace a routing rule keyed by its local-part. Replacing preserves the existing identifier if a row with the same local-part already exists. Returns the saved rule with populated.
