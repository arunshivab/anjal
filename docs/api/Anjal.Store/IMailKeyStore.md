# IMailKeyStore

**Namespace:** `Anjal.Store`

rc.25 (DES-15 §10): where the keys are stored.

## Members

- **AddMailboxKeysAsync** *(method)* - Store a mailbox's first keys, unless it has keys already (two first sign-ins at once): the keys stored first win, and false says so - the caller then reads them.
- **AddTenantKeysAsync** *(method)* - Store an organisation's first keys, unless it has keys already; false when another request stored them first.
- **DeleteGrantAsync** *(method)* - Take a key from a holder.
- **DeleteMailboxSessionKeysAsync** *(method)* - Forget every session's keys of a mailbox (sign out everywhere; a new password).
- **DeleteRestoreRequestAsync** *(method)* - Remove a restore request (used, or no longer needed).
- **DeleteSessionKeysAsync** *(method)* - Forget a session's keys (sign-out).
- **GetBreakGlassAsync** *(method)* - The break-glass key's server half, or null when none was made.
- **GetMailboxKeysAsync** *(method)* - A mailbox's keys, or null when the mailbox has none (individuals; before the move).
- **GetPublicKeysAsync** *(method)* - The public keys of many mailboxes at once (null entries are absent).
- **GetRestoreRequestAsync** *(method)* - A mailbox's restore request, or null.
- **GetSessionKeysAsync** *(method)* - A session's wrapped keys, or null (gone, expired, another mailbox's).
- **GetTenantKeysAsync** *(method)* - An organisation's keys, or null.
- **ListGrantsForHolderAsync** *(method)* - The keys given to a holder.
- **ListGrantsOfAsync** *(method)* - The holders of one key.
- **ListStoredTextsAsync** *(method)* - A mailbox's message texts exactly as stored, for the session's housekeeping: the rows still plain (to be sealed when the mailbox gets keys - the move), or the rows that arrived for the RSA key (to be re-keyed to the data key, DES-15 §5.3).
- **ListWaitingRestoresAsync** *(method)* - The restore requests waiting in an organisation (not yet granted).
- **PurgeSessionKeysAsync** *(method)* - Remove expired session keys.
- **SaveBreakGlassAsync** *(method)* - Keep the break-glass key's server half (made once, offline).
- **SaveGrantAsync** *(method)* - Give a key to a holder (replacing an earlier grant).
- **SaveMailboxKeysAsync** *(method)* - Change a mailbox's keys (a new password, a restore). The public key never changes: a row whose public key differs from the stored one is refused ().
- **SaveRestoreRequestAsync** *(method)* - Insert or replace a restore request.
- **SaveSessionKeysAsync** *(method)* - Keep a session's wrapped keys.
- **SaveStoredTextAsync** *(method)* - Replace a message's stored text.
- **SaveTenantKeysAsync** *(method)* - Change an organisation's keys (a sheet, the break-glass switch). The recovery public key never changes: a row whose recovery public key differs from the stored one is refused.
