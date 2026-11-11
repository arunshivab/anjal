# IImapAccount

**Namespace:** `Anjal.Imap`

One signed-in person's mail, for one IMAP session. Every call runs with their keys open.

## Members

- **AppendAsync** *(method)* - Add a message to a folder (APPEND).
- **CopyAsync** *(method)* - Copy a message to another folder.
- **CreateAsync** *(method)* - Make a folder.
- **DeleteAsync** *(method)* - Delete a message for good (EXPUNGE).
- **FoldersAsync** *(method)* - The folders.
- **MoveAsync** *(method)* - Move a message to another folder.
- **OpenAsync** *(method)* - Open a folder; null when there is none.
- **ReadAsync** *(method)* - A message's bytes, opened.
- **RemoveAsync** *(method)* - Remove an empty folder of the person's own.
- **RenameAsync** *(method)* - Rename a folder of the person's own.
- **SetFlagsAsync** *(method)* - Set a message's flags.
- **StillAllowedAsync** *(method)* - Whether the session may go on: the app password not removed, the mailbox and its organisation not switched off. Asked before every command and while idling; it may answer from a recent look.
- **Address** *(property)* - The address signed in.
