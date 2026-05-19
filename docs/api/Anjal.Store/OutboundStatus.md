# OutboundStatus

**Namespace:** `Anjal.Store`

Status of a queued outbound message.

## Members

- **Failed** *(field)* - Bounced (permanent failure or maximum retries reached).
- **Pending** *(field)* - Waiting to be sent (or retried after a transient failure).
- **Sending** *(field)* - Currently leased by a worker for a send attempt.
- **Sent** *(field)* - Successfully accepted by the destination.
