# SenderRuleRow

**Namespace:** `Anjal.Store`

A per-tenant sender allow/block rule. The pattern is either a full address (alice@example.com) or a domain with a leading "@" (@example.com, which also matches subdomains). Patterns are matched case-insensitively against the envelope MAIL FROM and the From header address. A block rule wins over an allow rule when both match; an exact-address rule wins over a domain rule.

## Members

- **Action** *(property)* - Allow or block.
- **CreatedAt** *(property)* - When the rule was created.
- **Id** *(property)* - Identifier assigned by the store.
- **MailboxId** *(property)* - Set for a personal rule, made by one mailbox's Report spam or Not spam; it affects only that mailbox. Null for a tenant-wide rule, which only an administrator sets, through the API.
- **Pattern** *(property)* - Address or "@domain" pattern, lowercase.
- **TenantId** *(property)* - The owning tenant.
