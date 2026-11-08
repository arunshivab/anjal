# DeskEventRow

**Namespace:** `Anjal.Store`

One line of a desk item's history (rc.27): who did what and when, never the mail's words.

## Members

- **ActorId** *(property)* - Who did it; null for Anjal (a mail arrived).
- **At** *(property)* - When.
- **Detail** *(property)* - The other person's id for an assignment, the note's id for a note; otherwise empty.
- **Id** *(property)* - Its number, in order.
- **ItemId** *(property)* - The item.
- **Kind** *(property)* - assigned, unassigned, taken, returned, replied, done, reopened, note, note-withdrawn.
