# MailboxRequest

**Namespace:** `Anjal.Api.Dto`

Request body for creating or updating a mailbox. The password is plaintext and is hashed via before persisting. On update, an omitted or empty password keeps the existing one.

## Members

- **Address** *(property)* - The full address local@domain (case-insensitive).
- **DisplayName** *(property)* - Display name for the From header.
- **Enabled** *(property)* - When false, delivery and authentication both fail.
- **Password** *(property)* - Plaintext password for submission auth. Empty on create means receive-only.
- **QuotaBytes** *(property)* - Soft quota in bytes. Null or 0 means the default (2 GiB).
- **TenantSlug** *(property)* - Slug of the owning tenant. The address domain must be registered to this tenant.
