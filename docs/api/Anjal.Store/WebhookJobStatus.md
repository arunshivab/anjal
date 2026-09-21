# WebhookJobStatus

**Namespace:** `Anjal.Store`

State of a queued webhook notification.

## Members

- **Delivered** *(field)* - The receiver answered 2xx.
- **Failed** *(field)* - Retried until its give-up time without success.
- **Pending** *(field)* - Waiting for its next attempt.
- **Sending** *(field)* - Leased by the worker; an attempt is in progress.
