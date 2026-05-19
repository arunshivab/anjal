# DkimSigner

**Namespace:** `Anjal.Dkim`

Signs RFC 5322 messages with a DKIM-Signature header (RFC 6376). Uses RSA-SHA256.

## Members

- **#ctor** *(method)* - Construct a signer.
- **Sign** *(method)* - Sign a message and return the modified message with a DKIM-Signature: header prepended.
