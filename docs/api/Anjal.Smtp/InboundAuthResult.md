# InboundAuthResult

**Namespace:** `Anjal.Smtp`

Outcome of inbound authentication. Carries the formatted Authentication-Results header value to prepend to the message, and a flag indicating whether DMARC policy requires this server to reject the message.

## Members

- **Detail** *(property)* - Opaque structured data exposed to the sink. The Smtp layer doesn't interpret this; it's passed through to so the webhook payload can include full per-verifier detail.
- **HeaderValue** *(property)* - The Authentication-Results header value to prepend (no field-name prefix, no terminating CRLF).
- **RejectReason** *(property)* - SMTP reply text to send with the 550 when is true. Empty falls back to a generic message.
- **ShouldReject** *(property)* - True if DMARC published p=reject and authentication failed. When the server is configured to enforce DMARC, set this to refuse the message with SMTP 550 before sink dispatch.
