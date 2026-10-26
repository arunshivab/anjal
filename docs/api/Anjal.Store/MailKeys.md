# MailKeys

**Namespace:** `Anjal.Store`

Keys: making them, naming them, wrapping them, and deriving the keys that unlock them.

## Members

- **PepperSetting** *(field)* - The setting holding the pepper (32 random bytes, base64), kept out of the database.
- **RsaBits** *(field)* - RSA key size for mailbox, organisation and break-glass keys (DES-15 §2).
- **UnlockRounds** *(field)* - PBKDF2 rounds for the unlock key (the same as the password hash).
- **AddressMark** *(method)* - rc.25 (DES-15 §7.6): the mark of an address in a person's encrypted contacts and contact groups, so that arrival (away replies to contacts only, rules on a group) can ask "is this sender in it?" without the list itself being readable: an HMAC keyed from the pepper, which is not in the database.
- **LoadPrivate** *(method)* - Load a PKCS#8 private key.
- **NewKeyPair** *(method)* - Make an RSA key pair: (SubjectPublicKeyInfo, PKCS#8 private key).
- **NewSalt** *(method)* - A new salt for the unlock key.
- **NewSymmetricKey** *(method)* - A fresh 256-bit key.
- **PepperFromEnvironment** *(method)* - The pepper from the environment, or empty when none is set (development).
- **PepperProblem** *(method)* - Why a service must not start with the pepper as it is (null when it may): a pepper that is not 32 bytes of base64 always stops it; with the PostgreSQL store (a real deployment), a missing pepper does too - keys made without it would be locked out the day it is added.
- **PublicKeyId** *(method)* - The 8-byte id of a public key: the first bytes of its SHA-256.
- **PublicKeyIdText** *(method)* - The id of a public key as text.
- **PublicOf** *(method)* - The public key (SubjectPublicKeyInfo) of a PKCS#8 private key.
- **RecoveryWrapKey** *(method)* - The key that a recovery secret (the sheet's 32 bytes) opens the organisation's recovery key with.
- **SessionWrapKey** *(method)* - The key a session's cookie value opens the session's copy of the keys with.
- **SymmetricKeyId** *(method)* - The 8-byte id of a symmetric key (a hash of the key with a label; it reveals nothing of the key).
- **SymmetricKeyIdText** *(method)* - The id of a symmetric key as text.
- **UnlockKey** *(method)* - The person's unlock key (DES-15 §2): PBKDF2-SHA256 of the password with its own salt, then HKDF with the pepper - so a stolen database without the pepper cannot test guesses.
- **Unwrap** *(method)* - Unwrap a secret wrapped with ; null when the key is wrong.
- **Wrap** *(method)* - Wrap a secret (a private key, a data key) with a 32-byte key.
- **TestKeyBits** *(property)* - Tests only: a smaller key size (0, the default, means ).
