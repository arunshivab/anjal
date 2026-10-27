# DoNotSendEntry

**Namespace:** `Anjal.Store`

One address an organisation's applications must not send to again (rc.17, design review [7d]).

## Members

- **Added** *(property)* - When it was added.
- **Address** *(property)* - The address, lower case.
- **App** *(property)* - rc.26 (DES-14 §19): the application the entry is for; empty for every application of the organisation.
- **By** *(property)* - Who added it: an administrator's address, or "anjal" when the server did.
- **Code** *(property)* - rc.26 (DES-14 §19): hard_bounce, complaint, unsubscribe or manual; empty on an entry made before rc.26 (see ).
- **Reason** *(property)* - Why: "Mailbox gone", "No such domain", "Gave up 3 times", "Marked as spam", or what the administrator wrote.
- **SendId** *(property)* - rc.26: the API send that caused it, or null.
