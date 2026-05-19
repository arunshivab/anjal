# RoutingRuleRequest

**Namespace:** `Anjal.Api.Dto`

Request body for POST /api/routing-rules: create or update a routing rule for a local-part.

## Members

- **LocalPart** *(property)* - The local-part this rule matches, case-insensitive.
- **WebhookSecret** *(property)* - Hex-encoded shared secret used for HMAC-SHA256 signing of webhook payloads. The caller is responsible for keeping this private. Typical length is 32 bytes (64 hex chars).
- **WebhookUrl** *(property)* - HTTP(S) URL Anjal will POST inbound messages to.
