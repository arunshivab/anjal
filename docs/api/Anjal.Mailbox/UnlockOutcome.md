# UnlockOutcome

**Namespace:** `Anjal.Mailbox`

What happened when a person's password was used to open their mail.

## Members

- **Created** *(field)* - Made now (the first sign-in after the move to encryption): open, and the mail already stored must be moved.
- **Locked** *(field)* - Locked: the password was reset and an administrator has not restored the mail yet.
- **NotEncrypted** *(field)* - The mailbox has no keys and gets none (an individual).
- **Opened** *(field)* - Opened.
- **Restored** *(field)* - Opened through a restore an administrator granted (DES-15 §6.1).
