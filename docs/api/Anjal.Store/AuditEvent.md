# AuditEvent

**Namespace:** `Anjal.Store`

One entry in the append-only audit trail: who changed what, when. Admin API changes and security-relevant webmail actions (sign-ins, password changes) are recorded. Request bodies are never stored - they carry passwords, private keys and webhook secrets.

## Members

- **Action** *(property)* - What was done, e.g. POST /api/mailboxes or webmail.password.changed.
- **Actor** *(property)* - Who acted: api, a mailbox address, or system.
- **At** *(property)* - When it happened (UTC), assigned by the store.
- **Detail** *(property)* - Short outcome or context: a status code, a client address.
- **Id** *(property)* - Identifier assigned by the store.
- **RemoteAddress** *(property)* - The client address the action came from, when known.
- **Subject** *(property)* - What it was done to, e.g. an address or a path.
