# WebhookDelivery

**Namespace:** `Anjal.Store`

Outcome of a webhook delivery attempt. Persisted so that operators can inspect why a message didn't reach the destination application.

## Members

- **AttemptedAt** *(property)* - Time the delivery was attempted.
- **ErrorMessage** *(property)* - Error message if the call failed before returning a status.
- **Id** *(property)* - Identifier assigned by the store.
- **InboundMessageId** *(property)* - The inbound message this delivery relates to.
- **StatusCode** *(property)* - HTTP status code returned by the webhook, or 0 if no response.
- **Url** *(property)* - Webhook URL that was called.
