# ServerLocalDomainResolver

**Namespace:** `Anjal.Server`

Local-domain resolver that chains two sources: an env-var list and the persistent -backed local_domains table. A domain is local if it appears in either source.

## Members

- **#ctor** *(method)* - Construct with optional env-var list and optional store.
- **IsLocalAsync** *(method)* - _(no description)_
