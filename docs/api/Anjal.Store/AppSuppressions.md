# AppSuppressions

**Namespace:** `Anjal.Store`

rc.26 (DES-14 §19): the do-not-send list as the application API sees it - the organisation's one list (rc.17), each entry for one application or for all of them, with a code. Adding or removing through here gives the application the events suppression.added and suppression.removed.

## Members

- **Codes** *(field)* - The codes an entry may carry.
- **AddAsync** *(method)* - Put an address on an application's list; null when it was already held back for it.
- **AppliesTo** *(method)* - Whether an entry holds back an application's mail.
- **CheckRowAsync** *(method)* - The outbound worker's check for an API send's row just before it is sent: the reason, or null.
- **CodeOf** *(method)* - An entry's code: its own, or for an entry made before rc.26, worked out from its reason.
- **FindAsync** *(method)* - The entry that holds back an application's mail to an address, or null.
- **ListAsync** *(method)* - The entries that hold back an application's mail, newest first.
- **NoteFinalFailureAsync** *(method)* - The outbound worker's step for an API send that failed for good (DES-14 §17): a hard bounce puts the address on the application's list. No bounce notice is filed anywhere: the application has the event.
- **RemoveAsync** *(method)* - Take an address off an application's list. Through the API only hard_bounce and manual entries may be removed; a complaint or an unsubscribe is the recipient's own and only an administrator removes it. Returns null when removed, "not_found", or "removal_not_allowed".
