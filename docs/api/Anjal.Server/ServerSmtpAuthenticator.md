# ServerSmtpAuthenticator

**Namespace:** `Anjal.Server`

SMTP submission authenticator that chains three sources: an in-memory env-var default user (if configured), the persistent -backed smtp_users table (service accounts), and the -backed mailboxes table (a mailbox authenticates with its full address and may send as its own domain). The env-var user takes priority - useful for bootstrap and single-tenant deployments.

## Members

- **#ctor** *(method)* - Construct with optional env-var single user and optional DB-backed store. At least one must be non-empty for AUTH to succeed.
- **#ctor** *(method)* - Construct with optional env-var single user, optional DB-backed store and optional mailbox store.
- **AuthenticateAsync** *(method)* - _(no description)_
