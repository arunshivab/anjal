# TenantDomainResponse

**Namespace:** `Anjal.Api.Dto`

Response body for a tenant domain.

## Members

- **CreatedAt** *(property)* - When the domain was registered.
- **Domain** *(property)* - The domain, lowercase.
- **Id** *(property)* - Identifier assigned by the store.
- **TenantId** *(property)* - The owning tenant's id.
- **TenantSlug** *(property)* - The owning tenant's slug.
- **Verified** *(property)* - Whether ownership is verified (always true for admin-API inserts in this release).
