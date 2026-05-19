# Program

**Namespace:** `Anjal.Server`

Composition root for the Anjal mail server host process. Wires Store + Routing + Smtp + Mime + Api together and runs until cancellation. Environment variables: ANJAL_BIND - bind address, default "127.0.0.1" ANJAL_PORT - SMTP receiver port, default 2525 ANJAL_HOSTNAME - hostname for SMTP banner, default "anjal.localhost" ANJAL_POSTGRES - PostgreSQL connection string. If unset, an in-memory store is used (suitable for demos). ANJAL_OUTBOUND_MODE - "direct" (MX-based, requires port 25 outbound), "relay" (single upstream, requires ANJAL_RELAY_HOST), or "none" (no outbound, inbound-only). Default "none". ANJAL_RELAY_HOST - upstream host for relay mode. ANJAL_RELAY_PORT - upstream port for relay mode, default 587. ANJAL_API_PORT - HTTP API port. If unset or 0, the API is disabled. ANJAL_API_TOKEN - bearer token clients must present in Authorization headers. Empty disables auth (test-only mode). ANJAL_API_BIND - HTTP API bind address. Defaults to ANJAL_BIND.

## Members

- **Main** *(method)* - Entry point.
