# DeliveryOutcome

**Namespace:** `Anjal.Smtp`

Outcome the SMTP receiver should report back to the connected client after DATA. Influences the SMTP reply code emitted on the wire.

## Members

- **Accepted** *(field)* - Message was accepted and persisted. SMTP 250.
- **PermanentFailure** *(field)* - Permanent failure - reject (550). Useful for unknown recipients that managed to slip past RCPT validation.
- **TransientFailure** *(field)* - Transient failure - the sender should retry (450). Useful when the database is briefly unavailable or a webhook is unreachable.
