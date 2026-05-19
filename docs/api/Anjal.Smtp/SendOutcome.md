# SendOutcome

**Namespace:** `Anjal.Smtp`

Outcome classification for an outbound attempt.

## Members

- **PermanentFailure** *(field)* - Permanent failure (5xx). Do not retry; the message has bounced.
- **Sent** *(field)* - All recipients accepted. No retry.
- **TransientFailure** *(field)* - Transient failure (4xx, connection error, timeout). Retry later.
