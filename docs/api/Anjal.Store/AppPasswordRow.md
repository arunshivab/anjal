# AppPasswordRow

**Namespace:** `Anjal.Store`

rc.28 (DES-20 §2): a password made for one mail app on one device. It reads and sends mail only; when the mailbox is encrypted, it opens its own copy of the mail key, which is deleted with it.

## Members

- **CreatedAt** *(property)* - When it was made.
- **Hash** *(property)* - The password's PBKDF2 hash, as account passwords are kept.
- **Id** *(property)* - Its id.
- **LastUsedAt** *(property)* - When an app last signed in with it.
- **LastUsedFrom** *(property)* - From where (an address on the internet).
- **MailboxId** *(property)* - The mailbox.
- **Name** *(property)* - The device's name, as the person gave it.
- **PrivateByApp** *(property)* - The mail key wrapped with the app password's key; null for a mailbox that is not encrypted.
- **RemovedAt** *(property)* - When it was removed; null while it works.
- **UnlockSalt** *(property)* - The salt of the key made from the app password.
