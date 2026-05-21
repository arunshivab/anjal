# ServerSmtpAuthenticator

**Namespace:** `Anjal.Server`

SMTP submission authenticator that chains two sources: an in-memory env-var default user (if configured) and the persistent -backed user table. The env-var user takes priority - useful for bootstrap and single-tenant deployments. Multi-tenant deployments use the store.

## Members

- **#ctor** *(method)* - Construct with optional env-var single user and optional DB-backed store. At least one must be non-empty for AUTH to succeed.
- **AuthenticateAsync** *(method)* - _(no description)_
