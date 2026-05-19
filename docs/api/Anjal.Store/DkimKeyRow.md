# DkimKeyRow

**Namespace:** `Anjal.Store`

A DKIM signing key persisted by Anjal.Store. The PEM is stored in plaintext; protect at the database access layer (encryption at rest, connection-level TLS, restricted role grants).

## Members

- **Domain** *(property)* - Sender domain this key signs for (case-insensitive). For example "mail.lipihis.in" or "noreply.sigma.com".
- **Id** *(property)* - Identifier assigned by the store.
- **PrivateKeyPem** *(property)* - RSA private key in PKCS#8 PEM form.
- **Selector** *(property)* - The DKIM selector, e.g. "default" or "2026a". Joins with the domain to form the DNS TXT record name selector._domainkey.domain.
- **UpdatedAt** *(property)* - When the key was created or last updated.
