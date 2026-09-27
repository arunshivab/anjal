# TlsDecision

**Namespace:** `Anjal.Smtp`

The one place that decides whether an outbound message is encrypted, sent in plain text or held (v1.0.0-rc.7). Both senders use it, so the owner's rule - never send mail unencrypted - cannot be applied in one and missed in the other.

## Members

- **Decide** *(method)* - Decide what to do once the server's EHLO reply is known.
