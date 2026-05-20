# Program

**Namespace:** `Anjal.Server`

Composition root for the Anjal mail server host process. Wires Store + Routing + Smtp + Mime + Api together and runs until cancellation. Environment variables: ANJAL_BIND - bind address, default "127.0.0.1" ANJAL_PORT - SMTP receiver port, default 2525 ANJAL_HOSTNAME - hostname for SMTP banner, default "anjal.localhost" ANJAL_POSTGRES - PostgreSQL connection string. If unset, an in-memory store is used (suitable for demos). ANJAL_OUTBOUND_MODE - "direct" (MX-based, requires port 25 outbound), "relay" (single upstream, requires ANJAL_RELAY_HOST), or "none". Default "none". ANJAL_RELAY_HOST - upstream host for relay mode. ANJAL_RELAY_PORT - upstream port for relay mode, default 587. ANJAL_API_PORT - HTTP API port. If unset or 0, the API is disabled. ANJAL_API_TOKEN - bearer token clients must present. ANJAL_API_BIND - HTTP API bind address. Defaults to ANJAL_BIND. ANJAL_TLS_CERT_PATH - path to fullchain.pem (with private key, or use ANJAL_TLS_KEY_PATH). ANJAL_TLS_KEY_PATH - path to privkey.pem if not embedded in fullchain. ANJAL_TLS_REQUIRE - if "true", server refuses MAIL FROM until STARTTLS. Default false. ANJAL_TLS_VALIDATE_PEER - if "false", outbound TLS skips cert validation (testing only). Default true. ANJAL_TLS_DEFAULT_MODE - default outbound TLS mode if no per-domain policy: "opportunistic" (default), "required", or "disabled".

## Members

- **BuildDkimResolver** *(method)* - Build a DKIM key resolver from env vars (single default key) chained with the store (per-domain overrides). Returns (resolver, requireDkim). If nothing is configured, resolver is null and DKIM is fully disabled.
- **BuildInboundAuth** *(method)* - Build the inbound SPF/DKIM/DMARC authenticator from env-var config.
- **Main** *(method)* - Entry point.
