# IDkimKeyResolver

**Namespace:** `Anjal.Dkim`

Resolves a for a sender domain. The OutboundWorker calls this once per outbound message, parsing the From header to extract the domain. If no key is found, the worker hard-fails the send (the "refuse to send unsigned" policy).

## Members

- **ResolveAsync** *(method)* - Look up the signing key for a sender domain. Returns null if no key is configured for that domain.
