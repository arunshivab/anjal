# HttpWebhookDispatcher

**Namespace:** `Anjal.Routing`

HTTP-based implementation that signs requests with HMAC-SHA256 and includes a Unix timestamp for replay protection. Header conventions: X-Anjal-Timestamp - Unix seconds at time of send. X-Anjal-Signature - sha256=<hex> over timestamp + "." + body.

## Members

- **#ctor** *(method)* - Construct the dispatcher with a pre-built .
- **ComputeSignature** *(method)* - Compute the HMAC-SHA256 signature over timestamp + "." + body. Exposed so receivers can validate using the same code.
- **SendAsync** *(method)* - _(no description)_
- **SerializePayload** *(method)* - Serialise a payload to canonical JSON. Hand-written to avoid taking a dependency on System.Text.Json - the schema is small and stable.
