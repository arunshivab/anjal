# KeyGrant

**Namespace:** `Anjal.Store`

A key given to a holder (an administrator's copy of the recovery key; a member's copy of a shared mailbox key).

## Members

- **OrgAdmin** *(field)* - An administrator's copy of the organisation's recovery key.
- **SharedMember** *(field)* - A member's copy of a shared mailbox's private key.
- **#ctor** *(method)* - A key given to a holder (an administrator's copy of the recovery key; a member's copy of a shared mailbox key).
- **Body** *(property)* - The private key sealed to the holder's public key.
- **HolderId** *(property)* - The holder's mailbox.
- **KeyOwner** *(property)* - Whose key: the organisation or the shared mailbox.
- **Kind** *(property)* - or .
