# IRecipientResolver

**Namespace:** `Anjal.Smtp`

Pluggable resolver for "is this domain local to me?" The MTA port (typically 25) uses this to refuse relaying mail for non-local destinations - i.e., the open-relay guard.

## Members

- **ExistsAsync** *(method)* - True when mail for this address can be delivered, false when it certainly cannot. Returns null when it cannot be determined - the caller then accepts the recipient, so no uncertainty ever becomes a refusal.
