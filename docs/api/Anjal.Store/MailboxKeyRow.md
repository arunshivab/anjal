# MailboxKeyRow

**Namespace:** `Anjal.Store`

rc.25 (DES-15 §2, §10): a mailbox's keys as stored - nothing in it opens anything alone.

## Members

- **CreatedAt** *(property)* - When made.
- **DataKey** *(property)* - The data key sealed to this mailbox's public key.
- **MailboxId** *(property)* - The mailbox.
- **PrivateByOrg** *(property)* - The private key sealed to the organisation's recovery key.
- **PrivateByPassword** *(property)* - The private key wrapped with the unlock key (password + salt + pepper); null until the person has a password here.
- **PublicKey** *(property)* - The RSA public key (SubjectPublicKeyInfo): arriving mail is sealed to it.
- **UnlockSalt** *(property)* - The salt of the person's unlock key.
- **UpdatedAt** *(property)* - When last changed.
