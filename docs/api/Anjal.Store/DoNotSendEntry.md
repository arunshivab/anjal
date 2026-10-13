# DoNotSendEntry

**Namespace:** `Anjal.Store`

One address an organisation's applications must not send to again (rc.17, design review [7d]).

## Members

- **Added** *(property)* - When it was added.
- **Address** *(property)* - The address, lower case.
- **By** *(property)* - Who added it: an administrator's address, or "anjal" when the server did.
- **Reason** *(property)* - Why: "Mailbox gone", "No such domain", "Gave up 3 times", "Marked as spam", or what the administrator wrote.
