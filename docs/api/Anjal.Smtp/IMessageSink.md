# IMessageSink

**Namespace:** `Anjal.Smtp`

Sink that consumes accepted SMTP messages. Implementations route to the store, fire webhooks, log, etc. The composition root in the host project wires a concrete sink (typically a delegate to MIME-parse, store-save, webhook-fire) into the receiver.

## Members

- **DeliverAsync** *(method)* - Accept and process a delivered message. Returning a transient failure causes the SMTP client to be told to retry; returning a permanent failure causes the message to be rejected with 550.
