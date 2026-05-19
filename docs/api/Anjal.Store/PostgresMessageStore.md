# PostgresMessageStore

**Namespace:** `Anjal.Store`

PostgreSQL-backed implementation of . Uses Npgsql for connection management; each method opens and closes its own connection through the driver's connection pool. Connection-string is supplied at construction time.

## Members

- **#ctor** *(method)* - Create the store with a PostgreSQL connection string.
- **CreateTagGrantAsync** *(method)* - _(no description)_
- **DeleteRoutingRuleAsync** *(method)* - _(no description)_
- **EnqueueOutboundAsync** *(method)* - _(no description)_
- **GetActiveTagGrantAsync** *(method)* - _(no description)_
- **GetInboundByIdAsync** *(method)* - _(no description)_
- **GetOutboundByIdAsync** *(method)* - _(no description)_
- **GetRoutingRuleAsync** *(method)* - _(no description)_
- **LeaseOutboundBatchAsync** *(method)* - _(no description)_
- **ListRoutingRulesAsync** *(method)* - _(no description)_
- **MarkOutboundResultAsync** *(method)* - _(no description)_
- **SaveInboundMessageAsync** *(method)* - _(no description)_
- **SaveWebhookDeliveryAsync** *(method)* - _(no description)_
- **UpsertRoutingRuleAsync** *(method)* - _(no description)_
