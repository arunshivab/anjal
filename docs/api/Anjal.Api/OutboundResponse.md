# OutboundResponse

**Namespace:** `Anjal.Api.Dto`

Response body for an outbound message - both the immediate enqueue reply and the status-fetch endpoint.

## Members

- **Attempts** *(property)* - Number of send attempts so far.
- **CreatedAt** *(property)* - When the message was enqueued.
- **EnvelopeFrom** *(property)* - SMTP envelope sender.
- **EnvelopeTo** *(property)* - SMTP envelope recipient.
- **GiveUpAt** *(property)* - Give-up deadline.
- **Id** *(property)* - Identifier assigned by the store.
- **LastError** *(property)* - Most recent reply text or error.
- **NextAttemptAt** *(property)* - Earliest time of the next send attempt.
- **Status** *(property)* - Current status as a string ("Pending", "Sending", "Sent", "Failed").
