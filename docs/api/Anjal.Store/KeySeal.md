# KeySeal

**Namespace:** `Anjal.Store`

DES-11 S6 (owner, 10 Oct 2026, "A"): a DKIM private key locked with the mail server's public "seal" key, so that the webmail - which makes the keys - holds no secret at all, and only the mail server, which signs, can open them. The sealed value also carries the DKIM public key in the clear, so the webmail can still show each domain the DNS record to publish. Format: seal1:<DKIM public key, SPKI base64>:<AES key wrapped with RSA-OAEP-SHA256, base64>:<nonce + ciphertext + tag, base64>. The private key PEM is encrypted with AES-256-GCM under a fresh random key; the domain and selector are bound in as associated data, so a sealed key cannot be moved to another domain.

## Members

- **Prefix** *(field)* - The prefix of a sealed value.
- **PublicRecordKind** *(field)* - The service record that publishes the seal's public key (and when it was made).
- **DkimPublicKey** *(method)* - The DKIM public key carried by a sealed value (SubjectPublicKeyInfo DER), or null.
- **IsSealed** *(method)* - True when a stored value is sealed this way.
- **Open** *(method)* - Open a sealed DKIM key with the mail server's private seal key.
- **ReadRecordAsync** *(method)* - The published record of the seal key, or null when none is published.
- **Seal** *(method)* - Seal a DKIM private key with the mail server's public seal key.
