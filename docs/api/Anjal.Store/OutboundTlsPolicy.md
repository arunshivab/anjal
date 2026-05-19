# OutboundTlsPolicy

**Namespace:** `Anjal.Store`

TLS policy for outbound sends to a specific destination domain. If no policy exists for a domain, the configured default policy is used.

## Members

- **Domain** *(property)* - Destination domain this policy applies to (case-insensitive). For example, "gmail.com", "partner-hospital.example". In relay mode, the lookup is against the relay's hostname.
- **Id** *(property)* - Identifier assigned by the store.
- **Mode** *(property)* - The TLS mode to apply when sending to this domain.
- **UpdatedAt** *(property)* - When this policy was created or last updated.
