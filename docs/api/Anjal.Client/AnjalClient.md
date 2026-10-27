# AnjalClient

**Namespace:** `Anjal.Client`

rc.26 (DES-14): Anjal's application API for .NET. Every request carries the key; with a signing key, every request is signed (§11). Sends get an Idempotency-Key unless told not to, and are retried with the same key on 408, 429, 5xx, 409 idempotency_in_progress and timeouts, after Retry-After - so a retry never sends twice.

## Members

- **#ctor** *(method)* - Initializes a new instance of the class.
- **AcknowledgeAsync** *(method)* - "I have saved it on my side" (POST /inbox/{id}/ack; signed). Harmless twice.
- **AcknowledgeAsync** *(method)* - Acknowledge up to 500 mails (POST /inbox/ack; signed); the ids that were acknowledged.
- **AddSuppressionAsync** *(method)* - Suppress an address by hand (POST /suppressions).
- **AddWebhookAsync** *(method)* - Add a webhook (POST /webhooks); its secret is in the answer, this once.
- **Dispose** *(method)* - _(no description)_
- **EventsAsync** *(method)* - Events after the last one seen, oldest first (GET /events).
- **GetInboxMessageAsync** *(method)* - One mail's encrypted envelope (GET /inbox/{id}; signed). Decrypt it with the application's private key.
- **GetMessageAsync** *(method)* - A message's status (GET /messages/{id}).
- **GetSuppressionAsync** *(method)* - Whether an address is suppressed, and why (GET /suppressions?address=).
- **HealthAsync** *(method)* - Whether the application can send now (GET /health): {ok, key, domains, limits, server}.
- **InboxAsync** *(method)* - Mail waiting in the inbox, oldest first (GET /inbox; signed).
- **LimitsAsync** *(method)* - The application's limits and today's use (GET /limits), as JSON.
- **RemoveSuppressionAsync** *(method)* - Take an address off the list (DELETE /suppressions/{address}): hard bounces and manual entries only.
- **SendAsync** *(method)* - Send one message (POST /api/v1/messages/email).
- **SendBatchAsync** *(method)* - Send up to 100 messages; each is checked and queued on its own (POST /messages/email/batch).
- **SigningString** *(method)* - The string a request is signed over (DES-14 §11): the method in capitals, the path and query exactly as sent, the Unix timestamp, and the lowercase hex SHA-256 of the body, joined by "\n".
- **AutoIdempotency** *(property)* - True (the default) to give every send an Idempotency-Key of its own.
- **Clock** *(property)* - Tests: the clock the signatures' timestamps read.
- **MaxAttempts** *(property)* - How many times a request is tried in all.
- **MaxRetryWait** *(property)* - The longest wait between tries, whatever Retry-After says.
