# RoutingRuleResponse

**Namespace:** `Anjal.Api.Dto`

Response body for routing-rule operations. The webhook secret is NEVER echoed back, even on the creating call - if the caller needs it, they must save it client-side at the moment of creation.

## Members

- **CreatedAt** *(property)* - When this rule was created.
- **Id** *(property)* - Identifier assigned by the store.
- **LocalPart** *(property)* - The local-part.
- **WebhookUrl** *(property)* - The webhook URL.
