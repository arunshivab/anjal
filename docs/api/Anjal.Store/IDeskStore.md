# IDeskStore

**Namespace:** `Anjal.Store`

rc.27: what shared desks and the draft clean-up need from the store.

## Members

- **AddDeskEventAsync** *(method)* - Add a line to an item's history.
- **AddDeskNoteAsync** *(method)* - Add a note.
- **GetDeskItemAsync** *(method)* - One item by its id.
- **GetOrAddDeskItemAsync** *(method)* - The item for a conversation on a desk, made when there is none (two at once get the same row).
- **ListAssignedDeskItemsAsync** *(method)* - The items assigned to a person and not done, on every desk.
- **ListDeskEventsAsync** *(method)* - An item's history, oldest first.
- **ListDeskEventsSinceAsync** *(method)* - A desk's history since a moment, every item, oldest first (for its figures).
- **ListDeskItemsAsync** *(method)* - A desk's items.
- **ListDeskNotesAsync** *(method)* - An item's notes, oldest first, withdrawn ones included.
- **ListEmptyDraftsAsync** *(method)* - Drafts saved empty and untouched since a moment, on every mailbox (D-309).
- **NoteDeskArrivalAsync** *(method)* - A mail arrived on a desk: the item is made, or a done one reopened (keeping its person), and its unanswered time starts if it had none.
- **NoteDeskReplyAsync** *(method)* - The desk replied in a conversation: nothing is owed until the next mail arrives.
- **SetDeskAssigneeAsync** *(method)* - Set who has an item, and nothing else: an arrival or a "done" at the same moment is kept.
- **SetDeskDoneAsync** *(method)* - Mark an item done, or open again, and nothing else: an assignment at the same moment is kept.
- **WithdrawDeskNoteAsync** *(method)* - Withdraw a note of one item; only its writer may.
