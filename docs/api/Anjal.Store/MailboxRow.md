# MailboxRow

**Namespace:** `Anjal.Store`

A mailbox: a receiving identity local_part@domain belonging to a tenant. The mailbox also carries submission credentials so that the same identity can authenticate on the submission port and send as itself (Dovecot/Postfix "virtual user" pattern).

## Members

- **DefaultQuotaBytes** *(field)* - Two gibibytes - the default per-mailbox quota.
- **Address** *(property)* - The full address local_part@domain.
- **CreatedAt** *(property)* - When the mailbox was created.
- **DisplayName** *(property)* - Display name for the From header (e.g. "Arun Shiva B").
- **Domain** *(property)* - Domain of the address, lowercase. Must be a domain of the tenant.
- **Enabled** *(property)* - When false, delivery and authentication both fail.
- **Id** *(property)* - Identifier assigned by the store.
- **LocalPart** *(property)* - Local-part of the address, lowercase (left of "@", no "+tag").
- **PasswordPbkdf2** *(property)* - PBKDF2 password hash in Anjal.Smtp.Pbkdf2Hasher format, used for submission authentication. Empty means the mailbox is receive-only and cannot authenticate.
- **QuotaBytes** *(property)* - Soft quota in bytes. Exceeding it is logged; hard enforcement is a later release.
- **TenantId** *(property)* - The owning tenant.
- **UpdatedAt** *(property)* - When the mailbox was created or last updated.
- **UsedBytes** *(property)* - Bytes currently stored in this mailbox, maintained by the store on each delivery.
