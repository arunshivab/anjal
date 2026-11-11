# ImportJobRow

**Namespace:** `Anjal.Store`

rc.28 (DES-20 §5): one job bringing old mail in.

## Members

- **Done** *(field)* - Finished.
- **Failed** *(field)* - Stopped by a problem (see ).
- **FileKind** *(field)* - A file the person uploaded.
- **Running** *(field)* - Running.
- **ServerKind** *(field)* - A copy from another mail server.
- **Stopped** *(field)* - Stopped by the person.
- **Waiting** *(field)* - Waiting to start.
- **CreatedAt** *(property)* - When it was made.
- **Cursor** *(property)* - Where it got to, so it can go on after a restart (the job's own words).
- **Error** *(property)* - Why it failed.
- **Folders** *(property)* - For a server, the folders chosen, separated by new lines.
- **Id** *(property)* - Its id.
- **Imported** *(property)* - Messages brought in.
- **Kind** *(property)* - or .
- **MailboxId** *(property)* - The mailbox.
- **Secret** *(property)* - For a server, its password sealed to the person's key; deleted when the job ends.
- **Seen** *(property)* - Messages seen so far.
- **Skipped** *(property)* - Messages skipped (already there, too large, a virus or a program).
- **Source** *(property)* - For a file, its path on the server; for a server, "host:port account".
- **State** *(property)* - The state.
- **TargetFolder** *(property)* - The folder to bring mail into; empty for one folder per old folder under "Imported".
- **Total** *(property)* - All the messages there are, once known; 0 until then.
- **UpdatedAt** *(property)* - When it last changed.
