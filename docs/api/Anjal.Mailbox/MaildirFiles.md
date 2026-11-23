# MaildirFiles

**Namespace:** `Anjal.Mailbox`

rc.30: renames and deletes of mail files that wait a moment while another process holds the file. On Windows a file another process has open (an antivirus scan of a mail just written, the search indexer) cannot be renamed or deleted until it lets go, and that moment used to come back to the webmail as a server error (found by the owner's run of Rc23Tests on Windows, 20 Nov 2026). Only that case waits: any other error is thrown at once.

## Members

- **Attempts** *(field)* - How many times a rename or delete is tried before the error is passed on.
- **Delete** *(method)* - Deletes a file, trying again for a moment while another process holds it.
- **IsHeldByAnother** *(method)* - Whether an error says another process has the file open (Windows' sharing and lock violations, 32 and 33); a missing file or folder never is.
- **Move** *(method)* - Renames a file, trying again for a moment while another process holds it.
- **Move** *(method)* - Renames a file with the given rename and wait, so the waiting can be tested.
