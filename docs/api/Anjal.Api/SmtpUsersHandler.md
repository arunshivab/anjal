# SmtpUsersHandler

**Namespace:** `Anjal.Api.Endpoints`

Endpoint handlers for /api/smtp-users. Passwords are accepted as plaintext in POST bodies, hashed via PBKDF2 before persisting, and NEVER returned by GET responses (no hash field in the response DTO).

## Members

- **#ctor** *(method)* - Construct.
- **DeleteAsync** *(method)* - DELETE /api/smtp-users/{username} - remove.
- **ListAsync** *(method)* - GET /api/smtp-users - list users (hashes never returned).
- **PostAsync** *(method)* - POST /api/smtp-users - create or update.
