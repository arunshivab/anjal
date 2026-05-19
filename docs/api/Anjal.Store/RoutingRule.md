# RoutingRule

**Namespace:** `Anjal.Store`

A routing rule stored in the address table. Maps an inbound local-part (the left side of an "@" address) to a webhook URL that the dispatcher will POST messages to.

## Members

- **CreatedAt** *(property)* - When this rule was created.
- **Id** *(property)* - Identifier assigned by the store.
- **LocalPart** *(property)* - The local-part this rule matches, case-insensitive.
- **WebhookSecret** *(property)* - Secret used for HMAC-SHA256 signing of webhook payloads. Stored hex-encoded.
- **WebhookUrl** *(property)* - HTTP(S) URL to POST the parsed message to.
