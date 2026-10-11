# AuditEvent

**Namespace:** `Anjal.Store`

One entry in the append-only audit trail: who changed what, when. Admin API changes and security-relevant webmail actions (sign-ins, password changes) are recorded. Request bodies are never stored - they carry passwords, private keys and webhook secrets.

## Members

- **ComputeChain** *(method)* - The chain value for an entry following .
- **Action** *(property)* - What was done, e.g. POST /api/mailboxes or webmail.password.changed.
- **Actor** *(property)* - Who acted: api, a mailbox address, or system.
- **At** *(property)* - When it happened (UTC), assigned by the store.
- **Chain** *(property)* - The chain (rc.13): SHA-256 of the previous entry's chain and this entry, hex. An entry removed or changed breaks every chain after it. Empty for entries written before rc.13.
- **Detail** *(property)* - Short outcome or context: a status code, a client address.
- **Id** *(property)* - Identifier assigned by the store.
- **RemoteAddress** *(property)* - The client address the action came from, when known.
- **Seq** *(property)* - Its place in the trail, assigned by the store (rc.13).
- **Subject** *(property)* - What it was done to, e.g. an address or a path.
