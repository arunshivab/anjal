# StorageState

**Namespace:** `Anjal.Store`

Where a mailbox stands against its organisation's storage plan (DES-11 D2).

## Members

- **#ctor** *(method)* - Where a mailbox stands against its organisation's storage plan (DES-11 D2).
- **Full** *(property)* - True when the mailbox may neither receive nor send.
- **OwnLimit** *(property)* - Its own limit; 0 for none of its own.
- **Percent** *(property)* - The figure the warnings go by, in percent: the mailbox's own use; under a shared plan the shared total's; under a mixed plan the reserve's once the mailbox's own space is used.
- **Plan** *(property)* - "none", "person", "shared" or "mixed".
- **Shared** *(property)* - The shared total or reserve; 0 when the plan has none.
- **SharedUsed** *(property)* - How much of it is used.
- **Used** *(property)* - The mailbox's own use.
