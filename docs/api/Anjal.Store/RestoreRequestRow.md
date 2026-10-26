# RestoreRequestRow

**Namespace:** `Anjal.Store`

A restore request (DES-15 §6.1): made when a person sets a new password without the old one.

## Members

- **CreatedAt** *(property)* - When the request was made.
- **Granted** *(property)* - The mailbox private key sealed to the restore public key, once an administrator restored it.
- **GrantedAt** *(property)* - When.
- **GrantedBy** *(property)* - Who restored it.
- **MailboxId** *(property)* - The mailbox.
- **RestoreByUnlock** *(property)* - The restore private key wrapped with the person's new unlock key.
- **RestorePublic** *(property)* - The one-time restore public key.
- **UnlockSalt** *(property)* - The salt of that unlock key.
