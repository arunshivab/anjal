# TenantRequest

**Namespace:** `Anjal.Api.Dto`

Request body for creating or updating a tenant.

## Members

- **DisplayName** *(property)* - Human-readable name.
- **Enabled** *(property)* - When false, no mail is delivered to the tenant's mailboxes.
- **EvidenceRetentionDays** *(property)* - Days evidence of deleted mail is kept (v1.0.0-rc.8; 1 to 36500). Omitted: unchanged (1095 for a new tenant).
- **PostmasterMailbox** *(property)* - Mailbox receiving postmaster@ and abuse@ each of the tenant's domains (v1.0.0-rc.8). Omitted: unchanged. Empty: cleared - the tenant's first mailbox.
- **Slug** *(property)* - Unique slug: letters, digits and hyphens, 1-63 characters; stored lowercase.
- **SpamThreshold** *(property)* - Spam score at or above which mail is filed in Junk. Null keeps the default (5); 0 disables Junk filing.
- **UnencryptedFolder** *(property)* - Folder for mail that reaches the tenant unencrypted (v1.0.0-rc.7). Omitted: unchanged. Empty: cleared, so such mail stays in INBOX with a red open lock (the default).
