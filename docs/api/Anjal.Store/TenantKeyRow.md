# TenantKeyRow

**Namespace:** `Anjal.Store`

rc.25 (DES-15 §6): an organisation's recovery key and its break-glass copy.

## Members

- **BreakGlassOn** *(property)* - Whether the break-glass key may recover this organisation (D-240: on by default).
- **CreatedAt** *(property)* - When made.
- **RecoveryBySecret** *(property)* - The recovery private key wrapped with the key derived from the recovery secret (the sheet).
- **RecoveryByServer** *(property)* - The recovery private key wrapped with a key derived from the pepper, only until an administrator has the key and has confirmed a recovery sheet (DES-15 §6.2); null after.
- **RecoveryPublic** *(property)* - The recovery public key: every mailbox private key of the organisation is also sealed to it.
- **SecretByBreakGlass** *(property)* - The recovery secret sealed to the break-glass public key; null when switched off.
- **SecretCheck** *(property)* - A check of the recovery secret (its symmetric key id), to tell a typed sheet is right.
- **SheetConfirmedAt** *(property)* - When the administrator confirmed the sheet by typing it back; null until then.
- **TenantId** *(property)* - The organisation.
- **UpdatedAt** *(property)* - When last changed.
