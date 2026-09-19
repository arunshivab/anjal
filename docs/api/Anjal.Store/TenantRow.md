# TenantRow

**Namespace:** `Anjal.Store`

A tenant: one customer organisation hosted on an Anjal deployment. Every mailbox, domain and message belongs to exactly one tenant. The doubles as the on-disk directory name under the Maildir root, so it is restricted to lowercase letters, digits and hyphens.

## Members

- **CreatedAt** *(property)* - When the tenant was created.
- **DisplayName** *(property)* - Human-readable name (e.g. "imagiQa Healthcare Services").
- **Enabled** *(property)* - When false, no mail is delivered to any mailbox of this tenant.
- **Id** *(property)* - Identifier assigned by the store.
- **Slug** *(property)* - Unique, URL-safe and filesystem-safe identifier (e.g. "imagiqa"). Lowercase letters, digits and hyphens only; 1-63 characters.
