# ILocalDomainResolver

**Namespace:** `Anjal.Smtp`

Pluggable resolver for "is this domain local to me?" The MTA port (typically 25) uses this to refuse relaying mail for non-local destinations - i.e., the open-relay guard.

## Members

- **IsLocalAsync** *(method)* - Check whether a domain is considered local (Anjal serves mail for it). Comparison should be case-insensitive.
