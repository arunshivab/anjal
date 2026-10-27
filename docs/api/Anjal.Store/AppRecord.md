# AppRecord

**Namespace:** `Anjal.Store`

rc.26: what the mail server needs of an application - read from the organisation's "apps" document, which the webmail's console writes (its OrgApp has these fields and more).

## Members

- **EncryptionKeys** *(property)* - Its encryption keys; new mail uses the newest.
- **Id** *(property)* - The application's id.
- **InboxMailboxId** *(property)* - Its hidden mailbox, once it has inbox addresses.
- **InboxQuotaBytes** *(property)* - Its inbox's storage limit, in bytes (2 GB unless the administrator changed it).
- **Name** *(property)* - Its name.
- **NewestKey** *(property)* - The key new mail is encrypted to, or null when none is uploaded.
- **PatientData** *(property)* - The operator's patient-data mark (DES-14 §20).
- **Retired** *(property)* - When it was retired, or null.
