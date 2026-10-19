# TenantKind

**Namespace:** `Anjal.Mailbox`

What kind of tenant an organisation record is (rc.22): an organisation, or the "individuals" tenant that holds people with their own address on Anjal's shared domain (anjalmail.com), each on their own, with no organisation around them. Kept as the tenant's "kind" document, written by the Anjal console when the tenant is made.

## Members

- **Kind** *(field)* - The tenant document kind.
- **IsIndividualsAsync** *(method)* - Whether a tenant is the individuals tenant.
- **WriteAsync** *(method)* - Record a tenant's kind.
- **Individuals** *(property)* - True for the individuals tenant.
