# ServerLocalDomainResolver

**Namespace:** `Anjal.Server`

Local-domain resolver that chains three sources: an env-var list, the persistent -backed local_domains table, and the -backed tenant_domains table. A domain is local if it appears in any source, so registering a tenant domain makes it RCPT-able immediately.

## Members

- **#ctor** *(method)* - Construct with optional env-var list and optional store.
- **#ctor** *(method)* - Construct with optional env-var list, optional store and optional mailbox store.
- **IsLocalAsync** *(method)* - _(no description)_
