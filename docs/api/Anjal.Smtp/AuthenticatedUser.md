# AuthenticatedUser

**Namespace:** `Anjal.Smtp`

A successfully authenticated SMTP submission user. Carried on after auth completes so the sink and downstream routing can attribute the submission.

## Members

- **AllowedFromDomains** *(property)* - Domains the user is permitted to send MAIL FROM as. If empty, the user can send from any domain (admin-level authority). If non-empty, MAIL FROM:<addr> is rejected unless addr's domain is in this list.
- **Username** *(property)* - The username under which the client authenticated.
