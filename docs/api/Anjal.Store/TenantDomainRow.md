# TenantDomainRow

**Namespace:** `Anjal.Store`

A domain owned by a tenant. Mail addressed to anything@domain is looked up against the tenant's mailboxes. A domain belongs to at most one tenant across the whole deployment.

## Members

- **CreatedAt** *(property)* - When the domain was registered.
- **Domain** *(property)* - The domain, lowercase (e.g. "anjal.co.in").
- **Id** *(property)* - Identifier assigned by the store.
- **TenantId** *(property)* - The owning tenant.
- **Verified** *(property)* - Whether ownership has been verified. Admin-API inserts are treated as verified; a self-service verification flow (DNS TXT challenge) is planned for a later release and will set this to false until the challenge passes.
