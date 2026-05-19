# RoutingMessageSink

**Namespace:** `Anjal.Server`

Production implementation. For each accepted SMTP message: parses the MIME, persists it, resolves the recipient, and fires a signed webhook.

## Members

- **#ctor** *(method)* - Construct the sink.
- **DeliverAsync** *(method)* - _(no description)_
