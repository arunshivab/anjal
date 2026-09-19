# TenantsHandler

**Namespace:** `Anjal.Api.Endpoints`

Endpoint handlers for /api/tenants and /api/tenant-domains. Tenant creation is admin-API-only: there is no self-service signup and no domain-verification flow in this release - registering a domain through this API marks it verified.

## Members

- **#ctor** *(method)* - Construct.
- **DeleteAsync** *(method)* - DELETE /api/tenants/{slug} - remove a tenant and everything under it (index only; Maildir files stay on disk).
- **DeleteDomainAsync** *(method)* - DELETE /api/tenant-domains/{domain} - unregister.
- **GetAsync** *(method)* - GET /api/tenants/{slug} - fetch one.
- **IsValidSlug** *(method)* - Validate a tenant slug: 1-63 characters of lowercase ASCII letters, digits and hyphens, not starting or ending with a hyphen.
- **ListAsync** *(method)* - GET /api/tenants - list.
- **ListDomainsAsync** *(method)* - GET /api/tenant-domains[?tenant=slug] - list domains.
- **PostAsync** *(method)* - POST /api/tenants - create or update.
- **PostDomainAsync** *(method)* - POST /api/tenant-domains - register a domain to a tenant.
