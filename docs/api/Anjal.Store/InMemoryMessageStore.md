# InMemoryMessageStore

**Namespace:** `Anjal.Store`

In-memory implementation of used in tests and the inbound-end-to-end example. Thread-safe via a single lock; not optimised for high concurrency. No persistence across process restarts.

## Members

- **CreateTagGrantAsync** *(method)* - _(no description)_
- **DeleteDkimKeyAsync** *(method)* - _(no description)_
- **DeleteOutboundTlsPolicyAsync** *(method)* - _(no description)_
- **DeleteRoutingRuleAsync** *(method)* - _(no description)_
- **EnqueueOutboundAsync** *(method)* - _(no description)_
- **GetActiveTagGrantAsync** *(method)* - _(no description)_
- **GetDkimKeyAsync** *(method)* - _(no description)_
- **GetInboundByIdAsync** *(method)* - _(no description)_
- **GetOutboundByIdAsync** *(method)* - _(no description)_
- **GetOutboundTlsPolicyAsync** *(method)* - _(no description)_
- **GetRoutingRuleAsync** *(method)* - _(no description)_
- **LeaseOutboundBatchAsync** *(method)* - _(no description)_
- **ListDkimKeysAsync** *(method)* - _(no description)_
- **ListOutboundTlsPoliciesAsync** *(method)* - _(no description)_
- **ListRoutingRulesAsync** *(method)* - _(no description)_
- **MarkOutboundResultAsync** *(method)* - _(no description)_
- **SaveInboundMessageAsync** *(method)* - _(no description)_
- **SaveWebhookDeliveryAsync** *(method)* - _(no description)_
- **UpsertDkimKeyAsync** *(method)* - _(no description)_
- **UpsertOutboundTlsPolicyAsync** *(method)* - _(no description)_
- **UpsertRoutingRuleAsync** *(method)* - _(no description)_
- **Deliveries** *(property)* - The webhook deliveries currently stored. Provided for inspection in tests.
- **Messages** *(property)* - The inbound messages currently stored. Provided for inspection in tests.
- **Outbound** *(property)* - The outbound messages currently queued or completed. Provided for inspection in tests.
- **Rules** *(property)* - The routing rules currently stored. Provided for inspection in tests.
