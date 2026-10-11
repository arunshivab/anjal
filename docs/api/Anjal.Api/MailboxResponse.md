# MailboxResponse

**Namespace:** `Anjal.Api.Dto`

Response body for a mailbox. The password hash is never returned.

## Members

- **Address** *(property)* - The full address local@domain.
- **CanAuthenticate** *(property)* - Whether the mailbox has submission credentials.
- **CreatedAt** *(property)* - When the mailbox was created.
- **DisplayName** *(property)* - Display name.
- **Domain** *(property)* - Domain.
- **Enabled** *(property)* - Whether the mailbox is enabled.
- **Id** *(property)* - Identifier assigned by the store.
- **LocalPart** *(property)* - Local-part.
- **QuotaBytes** *(property)* - The mailbox's size limit in bytes (0 when it has none of its own, under a shared storage plan).
- **TenantId** *(property)* - The owning tenant's id.
- **UpdatedAt** *(property)* - When the mailbox was last updated.
- **UsedBytes** *(property)* - Bytes currently stored.
