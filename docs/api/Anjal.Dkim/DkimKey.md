# DkimKey

**Namespace:** `Anjal.Dkim`

A DKIM signing key bound to a specific domain and selector. The selector becomes part of the DNS TXT record name: selector._domainkey.domain.

## Members

- **Domain** *(property)* - The sender domain this key signs for, e.g. "mail.lipihis.in". Used to match against the From header's domain.
- **PrivateKeyPem** *(property)* - RSA private key in PKCS#8 PEM form (-----BEGIN PRIVATE KEY----- ... -----END PRIVATE KEY-----).
- **Selector** *(property)* - The selector, e.g. "default" or "2026a". Becomes part of the DNS record name and the s= tag in the signature.
