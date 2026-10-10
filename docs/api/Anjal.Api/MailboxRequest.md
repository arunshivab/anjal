# MailboxRequest

**Namespace:** `Anjal.Api.Dto`

Request body for creating or updating a mailbox. The password is plaintext and is hashed via before persisting. On update, an omitted or empty password keeps the existing one.

## Members

- **Address** *(property)* - The full address local@domain (case-insensitive).
- **DisplayName** *(property)* - Display name for the From header.
- **Enabled** *(property)* - When false, delivery and authentication both fail.
- **Password** *(property)* - Plaintext password for submission auth. Empty on create means receive-only.
- **QuotaBytes** *(property)* - The mailbox's size limit in bytes, enforced (DES-11 D2). Null or 0 means the default (1 GiB), or the organisation's storage plan when it has one.
- **TenantSlug** *(property)* - Slug of the owning tenant. The address domain must be registered to this tenant.
