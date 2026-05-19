# OutboundRequest

**Namespace:** `Anjal.Api.Dto`

Request body for POST /api/outbound: enqueue an outbound message. Either must be supplied (a pre-built MIME message) OR the and fields (in which case Anjal builds a simple text/plain message).

## Members

- **BodyText** *(property)* - The plain-text body. Used when RawBytesBase64 is empty.
- **EnvelopeFrom** *(property)* - The SMTP envelope sender (no angle brackets).
- **EnvelopeTo** *(property)* - The SMTP envelope recipient (no angle brackets).
- **FromHeader** *(property)* - The From header. Used when RawBytesBase64 is empty.
- **GiveUpHours** *(property)* - Optional override for the give-up deadline in hours from now. Default is 24. Useful for less-time-sensitive messages.
- **RawBytesBase64** *(property)* - Base64-encoded RFC 5322 message bytes. Mutually exclusive with the structured fields below.
- **Subject** *(property)* - The Subject header. Used when RawBytesBase64 is empty.
- **ToHeader** *(property)* - The To header. Used when RawBytesBase64 is empty.
