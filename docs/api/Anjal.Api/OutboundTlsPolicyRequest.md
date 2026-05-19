# OutboundTlsPolicyRequest

**Namespace:** `Anjal.Api.Dto`

Request body for POST /api/outbound-tls-policies: create or update the TLS policy for a destination domain.

## Members

- **Domain** *(property)* - Destination domain (case-insensitive), e.g. "gmail.com".
- **Mode** *(property)* - One of "opportunistic", "required", "disabled".
