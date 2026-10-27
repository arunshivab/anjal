# InboxMessage

**Namespace:** `Anjal.Client`

rc.26 (DES-14 §18): one mail of the inbox as Anjal stores it - an envelope encrypted to the application's own RSA key: a fresh AES-256-GCM key per mail, wrapped with RSA-OAEP-SHA256. opens it with the application's private key.

## Members

- **Decrypt** *(method)* - Decrypt and read the mail.
- **DecryptRaw** *(method)* - The mail exactly as it arrived.
- **FromJson** *(method)* - Read an envelope.
- **Id** *(property)* - The mail's id.
- **Json** *(property)* - The envelope as Anjal gave it.
- **KeyId** *(property)* - The key it is encrypted to (the first 16 characters of its fingerprint).
- **Meta** *(property)* - What Anjal keeps readable beside it: receivedAt, size, spamScore, auth, inReplyToSend, risks.
