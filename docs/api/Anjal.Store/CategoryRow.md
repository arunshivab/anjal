# CategoryRow

**Namespace:** `Anjal.Store`

A named label a mailbox can put on a message. Categories exist at two levels: a tenant's defaults, shared by every mailbox in it and the only ones an institution can report across, and a mailbox's own additions, private to that mailbox. Colour comes from , one of eight validated slots. The eight are a per-mailbox budget: the tenant's defaults take slots in order, a mailbox's own take what remains, and anything past the eighth keeps and is told apart by name alone. Slots are stored, never derived from the name and never recomputed when a category is deleted, because recomputing repaints every chart.

## Members

- **MaxSlot** *(field)* - The highest colour slot the design system defines.
- **NoSlot** *(field)* - Slot value meaning "no colour left"; rendered in ink-subtle.
- **CreatedAt** *(property)* - When it was created.
- **Id** *(property)* - Identifier assigned by the store.
- **IsShared** *(property)* - True when this is a tenant default rather than a mailbox's own.
- **MailboxId** *(property)* - The mailbox that owns it, or null for a tenant default shared by every mailbox in the tenant.
- **Name** *(property)* - Display name, unique within its scope.
- **Slot** *(property)* - Colour slot 1-8, or .
- **TenantId** *(property)* - The tenant this category belongs to.
