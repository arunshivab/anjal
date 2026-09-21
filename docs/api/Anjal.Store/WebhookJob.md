# WebhookJob

**Namespace:** `Anjal.Store`

A webhook notification waiting to be delivered. Persisted when a message is accepted, so a notification survives a restart and a receiver that is briefly down still hears about every message. The payload is rebuilt at send time from the stored inbound message; the signing secret is read from the routing rule then, so it is never copied into the queue.

## Members

- **LeaseDuration** *(field)* - How long a lease lasts: well beyond one webhook's timeout.
- **Attempts** *(property)* - Attempts made so far.
- **AuthResultsJson** *(property)* - SPF/DKIM/DMARC results as JSON, captured at receipt.
- **CorrelationKey** *(property)* - Correlation key from the tag grant, if any.
- **CreatedAt** *(property)* - When the job was queued.
- **GiveUpAt** *(property)* - When to stop retrying.
- **Id** *(property)* - Identifier assigned by the store.
- **InboundMessageId** *(property)* - The stored inbound message this notification is about.
- **LastError** *(property)* - The last failure, for diagnosis.
- **LeaseExpiresAt** *(property)* - When a lease lapses (Sending only); a stale lease is taken again.
- **LocalPart** *(property)* - Local part used to find the routing rule at send time.
- **NextAttemptAt** *(property)* - When the next attempt is due.
- **Recipient** *(property)* - The recipient address as received.
- **Status** *(property)* - Current state.
- **Tag** *(property)* - Plus-tag, if any.
