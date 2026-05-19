# OutboundDelivery

**Namespace:** `Anjal.Smtp`

One outbound delivery instruction: a message and the recipients it should be sent to. Used by .

## Members

- **EnvelopeFrom** *(property)* - The MAIL FROM address (no angle brackets).
- **EnvelopeTo** *(property)* - The RCPT TO addresses (no angle brackets).
- **RawBytes** *(property)* - The raw RFC 5322 message bytes to send in the DATA phase.
