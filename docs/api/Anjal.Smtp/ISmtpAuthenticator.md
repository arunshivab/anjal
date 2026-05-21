# ISmtpAuthenticator

**Namespace:** `Anjal.Smtp`

Pluggable authenticator for SMTP submission. Called when a client issues AUTH PLAIN or AUTH LOGIN on a submission port. Implementations look up the credentials (env-var, database, LDAP, etc.) and either return an or null.

## Members

- **AuthenticateAsync** *(method)* - Verify credentials. Returns the authenticated user on success, or null on any failure (bad password, unknown user, disabled account). Implementations MUST NOT distinguish between these failure modes in the return value - that's important to prevent user enumeration.
