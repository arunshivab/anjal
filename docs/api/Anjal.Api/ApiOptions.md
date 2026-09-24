# ApiOptions

**Namespace:** `Anjal.Api`

Configuration for an .

## Members

- **MinimumTokenLength** *(field)* - The shortest admin token accepted: a short one is guessable, and this API can do anything.
- **AcmeDirectory** *(property)* - ACME certificate directory (see Anjal.Acme.CertificateStore). When set, GET /api/acme reports certificate and renewal status and POST /api/acme/renew requests an immediate renewal from whichever process hosts the renewal service. Null disables both routes.
- **BearerToken** *(property)* - Shared bearer token. Clients must send Authorization: Bearer <value>. Empty token disables auth - useful for local tests, never for production.
- **BindAddress** *(property)* - The IP address to bind. Loopback by default for local dev.
- **CertificateWarnDays** *(property)* - Days of certificate validity below which /healthz reports the TLS component as degraded. Default 7.
- **Log** *(property)* - Where to record detail that must not be returned to callers - the cause of a failed health probe, for instance (DEF-040 observation).
- **MaildirRoot** *(property)* - Maildir root to probe for writability in /healthz. Null skips the check.
- **MaxBodyBytes** *(property)* - Maximum request body size in bytes. Requests larger than this receive 413. Default 25 MB - matches the SMTP server's MaxMessageBytes.
- **Port** *(property)* - TCP port to listen on. 8080 is a common dev choice.
- **PreviousBearerToken** *(property)* - A token that is still accepted while it is being replaced. Rotation without downtime: set this to the old token, put the new one in , update the callers (SIGMA, Lipi), then remove this one and restart (SEC-R3).
