# AppEnvelope

**Namespace:** `Anjal.Store`

rc.26 (DES-14 §18, D-224, D-268): mail for an application's inbox, encrypted to the application's own RSA key before it is written. A fresh 256-bit key per mail, AES-256-GCM over the original bytes, the key wrapped with RSA-OAEP-SHA256. The stored file, and what /inbox/{id} returns, is one JSON envelope; Anjal holds no key that opens it.

## Members

- **Algorithm** *(field)* - The algorithm's name in the envelope.
- **MinimumBits** *(field)* - The smallest RSA key accepted, in bits.
- **Check** *(method)* - Read an application's public key (PEM, "BEGIN PUBLIC KEY"); the error when it is not an RSA public key of 3,072 bits or more.
- **IsEnvelope** *(method)* - Whether bytes look like an application envelope.
- **KeyIdOf** *(method)* - The key id an envelope names: the first 16 characters of the fingerprint.
- **MetaOf** *(method)* - The readable part of an envelope ("meta"), as JSON; "{}" when it cannot be read.
- **Open** *(method)* - Open an envelope with the application's private key (tests and tools; applications use Anjal.Client).
- **Seal** *(method)* - Encrypt a mail to an application's key.
