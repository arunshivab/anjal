# TenantResponse

**Namespace:** `Anjal.Api.Dto`

Response body for a tenant.

## Members

- **CreatedAt** *(property)* - When the tenant was created.
- **DisplayName** *(property)* - Human-readable name.
- **Enabled** *(property)* - Whether the tenant is enabled.
- **EvidenceRetentionDays** *(property)* - Days evidence of deleted mail is kept.
- **Id** *(property)* - Identifier assigned by the store.
- **PostmasterMailbox** *(property)* - Mailbox receiving postmaster@ and abuse@, or null for the tenant's first mailbox.
- **Slug** *(property)* - The slug.
- **SpamThreshold** *(property)* - Spam threshold in effect.
- **UnencryptedFolder** *(property)* - Folder for mail that arrives unencrypted, or null to keep it in INBOX with a red lock.
