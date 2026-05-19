# DkimKeyRequest

**Namespace:** `Anjal.Api.Dto`

Request body for POST /api/dkim-keys: upload or rotate a DKIM signing key for a sender domain.

## Members

- **Domain** *(property)* - Sender domain (case-insensitive). For example "mail.lipihis.in".
- **PrivateKeyPem** *(property)* - RSA private key in PKCS#8 PEM form (-----BEGIN PRIVATE KEY----- ... -----END PRIVATE KEY-----).
- **Selector** *(property)* - The selector (becomes the s= tag in the signature and part of the DNS TXT record name).
