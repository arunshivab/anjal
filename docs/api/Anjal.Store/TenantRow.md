# TenantRow

**Namespace:** `Anjal.Store`

A tenant: one customer organisation hosted on an Anjal deployment. Every mailbox, domain and message belongs to exactly one tenant. The doubles as the on-disk directory name under the Maildir root, so it is restricted to lowercase letters, digits and hyphens.

## Members

- **DefaultSpamThreshold** *(field)* - Default spam threshold for a new tenant.
- **CreatedAt** *(property)* - When the tenant was created.
- **DisplayName** *(property)* - Human-readable name (e.g. "imagiQa Healthcare Services").
- **Enabled** *(property)* - When false, no mail is delivered to any mailbox of this tenant.
- **EvidenceRetentionDays** *(property)* - Days evidence of deleted mail is kept (v1.0.0-rc.8; PRJ-03b D-11: 3 years by default).
- **Id** *(property)* - Identifier assigned by the store.
- **PostmasterMailbox** *(property)* - Where postmaster@ and abuse@ each of the tenant's domains are delivered (v1.0.0-rc.8), or null for the tenant's first mailbox.
- **Slug** *(property)* - Unique, URL-safe and filesystem-safe identifier (e.g. "imagiqa"). Lowercase letters, digits and hyphens only; 1-63 characters.
- **SpamThreshold** *(property)* - Messages scoring at or above this land in Junk instead of INBOX. Scores are integers; see Anjal.Spam.SpamScorer for the rules.
- **UnencryptedFolder** *(property)* - Folder that mail reaching this tenant unencrypted is filed in, or null (the default) to keep it in INBOX with a red open lock. Mail from a sender the recipient trusts is never filed away.
