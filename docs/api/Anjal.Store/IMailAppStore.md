# IMailAppStore

**Namespace:** `Anjal.Store`

rc.28 (DES-20): what mail apps and import need from the store.

## Members

- **AddAppPasswordAsync** *(method)* - Keep a new app password.
- **AddImportJobAsync** *(method)* - Keep a new import job.
- **AssignImapUidsAsync** *(method)* - A folder's IMAP numbers: UIDVALIDITY made the first time, and a UID given to each message that has none in this folder (in the order they arrived), atomically.
- **ClaimImportJobAsync** *(method)* - Take a job to run: true only for the one caller that moved it from waiting (or from running, when it has not changed since - a run stopped by a restart) to running.
- **ExpireChoosingImportJobsAsync** *(method)* - A job still waiting for its person to choose folders since before is stopped and its sealed password emptied (the person left the page).
- **GetImportJobAsync** *(method)* - One import job.
- **ListAppPasswordsAsync** *(method)* - A mailbox's app passwords that still work, newest first.
- **ListImportJobsAsync** *(method)* - A mailbox's import jobs, newest first.
- **ListOpenImportJobsAsync** *(method)* - Jobs waiting or running (to go on after a restart), oldest first.
- **RemoveAppPasswordAsync** *(method)* - Remove an app password: it stops at once and its key copy is deleted.
- **SaveImportJobAsync** *(method)* - Save a job's state, counts, cursor, error and secret. A job the person stopped stays stopped: only another "stopped" is saved over it, so a running import cannot undo Stop.
- **SetReceivedAtAsync** *(method)* - Give a message the time it arrived at first (mail imported, or appended by an app with its date).
- **TouchAppPasswordAsync** *(method)* - Note a sign-in with an app password.
