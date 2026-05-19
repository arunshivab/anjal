# TlsMode

**Namespace:** `Anjal.Store`

TLS handling mode for an outbound send to a particular destination.

## Members

- **Disabled** *(field)* - Do not use TLS even if advertised. Useful for testing and trusted local relays.
- **Opportunistic** *(field)* - Try STARTTLS, fall back to plaintext if the server does not advertise it.
- **Required** *(field)* - STARTTLS is mandatory. If the server does not advertise it, the send fails (transient).
