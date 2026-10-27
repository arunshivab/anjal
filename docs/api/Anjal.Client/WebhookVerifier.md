# WebhookVerifier

**Namespace:** `Anjal.Client`

rc.26 (DES-14 §8): checks that a webhook's post came from Anjal - Anjal-Signature: t=<unix>, v1=<hex HMAC-SHA256(secret, t + "." + body)> - and is recent.

## Members

- **Verify** *(method)* - Whether a post is Anjal's, within the tolerance.
