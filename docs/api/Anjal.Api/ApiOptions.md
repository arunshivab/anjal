# ApiOptions

**Namespace:** `Anjal.Api`

Configuration for an .

## Members

- **BearerToken** *(property)* - Shared bearer token. Clients must send Authorization: Bearer <value>. Empty token disables auth - useful for local tests, never for production.
- **BindAddress** *(property)* - The IP address to bind. Loopback by default for local dev.
- **MaxBodyBytes** *(property)* - Maximum request body size in bytes. Requests larger than this receive 413. Default 25 MB - matches the SMTP server's MaxMessageBytes.
- **Port** *(property)* - TCP port to listen on. 8080 is a common dev choice.
