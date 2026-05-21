# LocalDomainsHandler

**Namespace:** `Anjal.Api.Endpoints`

Endpoint handlers for /api/local-domains. The MTA port uses this list to refuse RCPT TO for non-local destinations (open-relay guard).

## Members

- **#ctor** *(method)* - Construct.
- **DeleteAsync** *(method)* - DELETE /api/local-domains/{domain} - remove.
- **ListAsync** *(method)* - GET /api/local-domains - list all.
- **PostAsync** *(method)* - POST /api/local-domains - register a domain.
