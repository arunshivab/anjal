# MailEnvelope

**Namespace:** `Anjal.Store`

rc.25 (DES-15, organisation-held encryption, D-232): the one envelope format for everything Anjal stores encrypted - a mail file, a subject, a body text, a document, a wrapped key. A fresh 256-bit content key encrypts the bytes with AES-256-GCM; the content key itself is wrapped either to an RSA public key (OAEP with SHA-256; kind 1 - mail arriving, the sender's copy, anything written without the reader's key) or with a 256-bit symmetric key (AES-GCM; kind 2 - re-keyed mail and what is written in a session). The header names the key by an 8-byte id so that the right key is found and keys can be rotated. "ANJE" | version 1 | kind | key id (8) | wrapped length (u16, big-endian) | wrapped content key | nonce (12) | tag (16) | ciphertext Only .NET's own System.Security.Cryptography is used.

## Members

- **FieldMarker** *(field)* - How every encrypted text field begins: the prefix and the base64url of the envelope's magic and version ("ANJE", 1). A plain text that merely starts with "e1." is not taken for one.
- **FieldPrefix** *(field)* - The prefix of an encrypted text field ("e1." then base64url of an envelope).
- **KindManyKeys** *(field)* - Kind 3: the content key wrapped to several RSA public keys (evidence kept for more than one organisation).
- **KindPublicKey** *(field)* - Kind 1: the content key wrapped to an RSA public key.
- **KindSymmetric** *(field)* - Kind 2: the content key wrapped with a 256-bit symmetric key.
- **Base64Url** *(method)* - base64url without padding.
- **Describe** *(method)* - The kind and key id of an envelope, or null when it is not one.
- **FieldEnvelope** *(method)* - The envelope inside a stored field, or null when the text is not encrypted.
- **FieldToPublicKey** *(method)* - Encrypt a text field to an RSA public key ("e1." + base64url).
- **FieldWithKey** *(method)* - Encrypt a text field with a symmetric key.
- **FromBase64Url** *(method)* - Decode base64url without padding.
- **IsEnvelope** *(method)* - True when the bytes are an envelope (the magic and the version).
- **IsField** *(method)* - True when a stored text is an encrypted field.
- **Open** *(method)* - Open an envelope with the keys the resolver can give.
- **OpenField** *(method)* - Open a stored field: the text, the text itself when it was never encrypted, or null when locked.
- **Rekey** *(method)* - Re-wrap an envelope's content key with a symmetric key, keeping the ciphertext as it is (DES-15 §5.3: mail that arrived for the RSA key is re-keyed to the mailbox data key at the next sign-in, so lists and search cost AES, not RSA). Null when the envelope cannot be opened here.
- **RekeyField** *(method)* - Re-key a stored field (§5.3); the field unchanged when it cannot be opened or is already re-keyed.
- **SealToPublicKey** *(method)* - Encrypt bytes to an RSA public key (kind 1).
- **SealToPublicKeys** *(method)* - Encrypt bytes to several RSA public keys at once (kind 3; one key gives kind 1).
- **SealWithKey** *(method)* - Encrypt bytes with a 256-bit symmetric key (kind 2).
