# MailKeyManager

**Namespace:** `Anjal.Mailbox`

rc.25 (DES-15): every operation on keys - making them, opening them with a password, keeping a session's copy, an organisation's recovery key and its sheet, an administrator restoring a person's mail, shared mailboxes. It never sees a password longer than one call, and never stores anything that opens a key on its own.

## Members

- **#ctor** *(method)* - Initializes a new instance of the class.
- **BreakGlassAsync** *(method)* - Anjal's break-glass key as this server knows it: the line in the settings () is kept in the database the first time it is seen, or when it is replaced (a new key, OPS-16); null when none was ever set.
- **ConfirmSheetAsync** *(method)* - The administrator typed the sheet back: it is confirmed, and the temporary copy of the recovery key is destroyed.
- **CreateMailboxKeysAsync** *(method)* - Make a mailbox's keys (a new person at registration, a person at their first sign-in after the move, a shared mailbox). Null when the mailbox is an individual's or already has keys.
- **EncryptsAsync** *(method)* - Whether an organisation's mail is encrypted: every organisation, not the individuals' service (D-233, D-234).
- **EnsureTenantKeysAsync** *(method)* - The organisation's keys, made now if it has none. A new recovery key is kept, until an administrator confirms a recovery sheet, with a key derived from the pepper (DES-15 §6.2).
- **FillAsync** *(method)* - Fill a keyring for a person's request: their own keys, the shared mailboxes given to them, and, for an administrator, their organisation's recovery key.
- **GrantAdminAsync** *(method)* - Give an administrator their copy of the organisation's recovery key.
- **GrantRestoreAsync** *(method)* - An administrator restores a person's mail (DES-15 §6.1): the person's private key, opened with the organisation's recovery key, is sealed to their restore request's one-time key.
- **GrantSharedAsync** *(method)* - Give a shared mailbox's key to a member (or replace it).
- **HoldKey** *(method)* - The webmail's key for mail waiting in Scheduled (DES-15 §7.4): derived from the pepper, never stored.
- **IssueSheetAsync** *(method)* - Make a new recovery sheet (DES-15 §6.2): a new 32-byte secret wraps the recovery key; with the break-glass key switched on and made, the secret is also sealed to it (D-240). Returns the secret to show once; it is never stored.
- **LoadSessionAsync** *(method)* - A session's keys, opened with the cookie's value; null when there are none or the cookie is wrong.
- **NewSessionToken** *(method)* - A new random value for a session's cookie.
- **OpenMailboxPrivateByOrgAsync** *(method)* - A mailbox's private key opened with the organisation's recovery key (restores, shared mailboxes, legal holds).
- **OpenRecoveryAsync** *(method)* - The organisation's recovery private key for an administrator: from their own grant (opened with their mailbox key), else from the temporary copy kept until the first sheet is confirmed.
- **OpenWithSheetAsync** *(method)* - The recovery private key from a typed sheet (no administrator left; or the break-glass secret given back to the organisation).
- **RevokeSharedAsync** *(method)* - Take a shared mailbox's key from a member.
- **RewrapAsync** *(method)* - A new password while the mail is open (Settings › Security; a session): the private key is wrapped with the new unlock key; no mail is re-encrypted.
- **SaveSessionAsync** *(method)* - Keep a session's copy of the keys, wrapped with the cookie's value (DES-15 §4).
- **SetBreakGlassAsync** *(method)* - Switch the break-glass key on or off for an organisation (D-240). Switching it on takes effect with the next sheet.
- **StartRestoreAsync** *(method)* - A new password without the old one (forgotten; reset by an administrator or the recovery address): the mail stays locked and a restore request is made with a one-time key that only the new password opens (DES-15 §6.1). False when the mailbox has no keys (nothing to restore).
- **UnlockAsync** *(method)* - Open a person's mail with their password at sign-in: their own wrap, else a granted restore, else (no keys yet) make them; locked when the password was reset and no restore was granted. The caller has already checked the password itself.
- **Store** *(property)* - The key store.
