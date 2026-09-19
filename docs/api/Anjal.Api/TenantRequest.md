# TenantRequest

**Namespace:** `Anjal.Api.Dto`

Request body for creating or updating a tenant.

## Members

- **DisplayName** *(property)* - Human-readable name.
- **Enabled** *(property)* - When false, no mail is delivered to the tenant's mailboxes.
- **Slug** *(property)* - Unique slug: lowercase letters, digits and hyphens, 1-63 characters.
- **SpamThreshold** *(property)* - Spam score at or above which mail is filed in Junk. Null keeps the default (5); 0 disables Junk filing.
