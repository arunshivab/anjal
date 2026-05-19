# OutboundMessage

**Namespace:** `Anjal.Store`

A queued outbound message. Created by the API layer or by a webhook auto-reply rule; consumed by an OutboundWorker background task that runs send attempts with exponential backoff.

## Members

- **Attempts** *(property)* - Number of send attempts made so far.
- **CreatedAt** *(property)* - Time the message was enqueued.
- **EnvelopeFrom** *(property)* - The SMTP envelope sender (no angle brackets).
- **EnvelopeTo** *(property)* - The SMTP envelope recipient (no angle brackets). One row per recipient.
- **GiveUpAt** *(property)* - Cutoff after which the message should be permanently failed regardless of remaining retry budget. Default is 24 hours after creation; the caller can override per-message.
- **Id** *(property)* - Identifier assigned by the store.
- **LastError** *(property)* - The reply text of the most recent attempt (success or failure).
- **NextAttemptAt** *(property)* - Earliest time the next send attempt should be tried.
- **RawBytes** *(property)* - The raw RFC 5322 message bytes to send in the DATA phase.
- **Status** *(property)* - Current status.
