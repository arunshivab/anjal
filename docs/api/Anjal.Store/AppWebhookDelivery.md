# AppWebhookDelivery

**Namespace:** `Anjal.Store`

rc.26 (DES-14 §8): one event's delivery to one webhook, with its retries.

## Members

- **AppId** *(property)* - The application.
- **At** *(property)* - When it was made, or last tried.
- **Attempts** *(property)* - Attempts made.
- **Code** *(property)* - The last HTTP status, or null.
- **Error** *(property)* - Why the last attempt failed.
- **EventSeq** *(property)* - The event.
- **Id** *(property)* - Its id.
- **NextAt** *(property)* - The next attempt, while pending.
- **Status** *(property)* - pending, delivered or failed.
- **WebhookId** *(property)* - The webhook.
