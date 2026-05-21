# SmtpUserRow

**Namespace:** `Anjal.Store`

An SMTP submission user, used to authenticate clients connecting to Anjal's submission port. Passwords are stored as PBKDF2-SHA256 hashes of the form pbkdf2$<iterations>$<salt-b64>$<hash-b64>. Never log or return the hash in API responses.

## Members

- **AllowedFromDomains** *(property)* - Domains the user can send MAIL FROM: as. Empty list means admin authority (any domain allowed).
- **Enabled** *(property)* - When false, authentication attempts fail regardless of password.
- **Id** *(property)* - Identifier assigned by the store.
- **PasswordPbkdf2** *(property)* - PBKDF2-SHA256 hash of the password in pbkdf2$iterations$salt-b64$hash-b64 form.
- **UpdatedAt** *(property)* - When the row was created or last updated.
- **Username** *(property)* - The username (case-insensitive in lookup, stored as provided).
