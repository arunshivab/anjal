# SmtpUserRequest

**Namespace:** `Anjal.Api.Dto`

Request body for creating or updating an SMTP submission user. The password field is the plaintext password; the API hashes it via before persisting.

## Members

- **AllowedFromDomains** *(property)* - Domains the user is permitted to send MAIL FROM as. Empty list means admin authority (any domain). Comparison is case-insensitive.
- **Enabled** *(property)* - When false, authentication attempts fail regardless of password.
- **Password** *(property)* - Plaintext password. Hashed before persisting.
- **Username** *(property)* - Unique username (case-insensitive).
