# OutboundMessage

**Namespace:** `Anjal.Store`

A queued outbound message. Created by the API layer or by a webhook auto-reply rule; consumed by an OutboundWorker background task that runs send attempts with exponential backoff.

## Members

- **CancelledBySender** *(field)* - The reason recorded when the sender cancels a waiting message from the Outbox (rc.12).
- **DefaultGiveUp** *(field)* - How long outgoing mail is retried by default: 5 days (v1.0.0-rc.9, DEF-068, D-53; RFC 5321 4.5.4.1 asks for at least 4 to 5 days).
- **LeaseDuration** *(field)* - How long a lease lasts. Longer than any single delivery attempt can take with its timeouts, so a slow but live send is never taken over.
- **Attempts** *(property)* - Number of send attempts made so far.
- **BodyRemovedAt** *(property)* - When the message's content was removed from the queue after its keep-days (rc.21.1, DEF-117, owner 16 Oct 2026): only the header block is kept, so the record of who, when and what happened stays; the untouched original stays in the evidence store. Null until then.
- **CreatedAt** *(property)* - Time the message was enqueued.
- **EnvelopeFrom** *(property)* - The SMTP envelope sender (no angle brackets).
- **EnvelopeTo** *(property)* - The SMTP envelope recipient (no angle brackets). One row per recipient.
- **FinishedAt** *(property)* - When delivery ended - sent, or given up (v1.0.0-rc.21.1, DEF-117). Null while it is still being tried. The organisation's keep-days for the Outbox copy count from here.
- **GiveUpAt** *(property)* - Cutoff after which the message should be permanently failed regardless of remaining retry budget. Default is after creation; the caller can override per-message.
- **Id** *(property)* - Identifier assigned by the store.
- **LastError** *(property)* - The reply text of the most recent attempt (success or failure).
- **LeaseExpiresAt** *(property)* - While , when the lease lapses. A worker that stops mid-batch (crash, restart, deploy) leaves its leased messages in Sending; once this time passes they are leased again rather than stranded. Null when not leased.
- **NextAttemptAt** *(property)* - Earliest time the next send attempt should be tried.
- **RawBytes** *(property)* - The raw RFC 5322 message bytes to send in the DATA phase.
- **Status** *(property)* - Current status.
