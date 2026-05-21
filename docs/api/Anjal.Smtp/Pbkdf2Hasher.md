# Pbkdf2Hasher

**Namespace:** `Anjal.Smtp`

PBKDF2-SHA256 password hasher for SMTP submission user passwords. Uses the BCL's directly - no external dependencies. Hash format is pbkdf2$<iterations>$<salt-b64>$<hash-b64>. The default 100,000 iterations matches NIST SP 800-132 guidance from 2024. For service-to-service credentials (which SMTP submission users are) this is conservative; user-facing login systems should consider higher counts.

## Members

- **DefaultIterations** *(field)* - Default iteration count.
- **HashBytes** *(field)* - Hash output length in bytes (32 = 256 bits).
- **SaltBytes** *(field)* - Salt length in bytes (16 = 128 bits, well above the 8-byte minimum).
- **Hash** *(method)* - Hash a password. Returns a self-describing string of the form pbkdf2$iterations$salt-b64$hash-b64.
- **Verify** *(method)* - Verify a password against a stored hash. Returns true on match, false on mismatch or any parse error. Uses a constant-time comparison to defend against timing attacks.
