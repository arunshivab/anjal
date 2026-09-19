# AcmeEnvironment

**Namespace:** `Anjal.Acme`

ACME configuration read from environment variables, shared by every Anjal host process so they agree on where the certificate lives. ANJAL_ACME_DIR - certificate directory. Default /var/lib/anjal/acme (Unix) or %LOCALAPPDATA%\Anjal\acme (Windows). ANJAL_ACME_DOMAINS - comma-separated DNS names for the certificate. Empty means ACME is not configured. ANJAL_ACME_EMAIL - account contact address (expiry notices). ANJAL_ACME_DIRECTORY - ACME directory URL. Default Let's Encrypt production. ANJAL_ACME_STAGING - "true" selects Let's Encrypt staging (ignored if ANJAL_ACME_DIRECTORY is set). ANJAL_ACME_KEY - "ecdsa-p256" (default) or "rsa-2048". ANJAL_ACME_RENEW_DAYS - renew when fewer days remain. Default 30. ANJAL_ACME_HOST - "true"/"false": whether THIS process runs the renewal service. Exactly one process per machine should. The webmail defaults to true when domains are configured; the server defaults to false. ANJAL_ACME_HTTP_BIND - address for the HTTP-01 responder when the server hosts renewal. Default "+" (all interfaces). ANJAL_ACME_HTTP_PORT - port for that responder. Default 80.

## Members

- **Read** *(method)* - Read the environment.
- **Configured** *(property)* - Whether domains were configured.
- **DefaultDirectory** *(property)* - Platform default certificate directory.
- **Directory** *(property)* - Certificate directory.
- **Domains** *(property)* - Configured domains; empty when ACME is off.
- **Host** *(property)* - Whether this process should run the renewal service.
- **HttpBind** *(property)* - HTTP-01 responder bind address (server-hosted mode).
- **HttpPort** *(property)* - HTTP-01 responder port (server-hosted mode).
- **Options** *(property)* - The renewal options built from the environment.
