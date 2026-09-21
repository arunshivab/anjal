# SecretProtector

**Namespace:** `Anjal.Store`

Envelope encryption for secrets held in the database - DKIM private keys today. Values are sealed with AES-256-GCM under a key-encryption key (KEK) that lives only in the server's environment file, never in the database. A stolen database dump, or a backup read without the KEK, yields ciphertext. Sealed values are stored as enc:v1: followed by base64 of nonce (12 bytes), ciphertext and tag (16 bytes). A stored value without that prefix is treated as plaintext, so keys written before encryption was configured keep working and are re-sealed the next time they are saved.

## Members

- **Prefix** *(field)* - Prefix marking a sealed value.
- **#ctor** *(method)* - Construct from a 32-byte key.
- **FromEnvironment** *(method)* - The protector configured by ANJAL_KEK (base64 of 32 random bytes, e.g. openssl rand -base64 32), or null when unset.
- **IsSealed** *(method)* - Whether a stored value is sealed.
- **Open** *(method)* - Open a sealed value; plaintext values are returned unchanged.
- **Seal** *(method)* - Seal a plaintext secret.
