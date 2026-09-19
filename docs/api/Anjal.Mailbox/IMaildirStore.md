# IMaildirStore

**Namespace:** `Anjal.Mailbox`

Filesystem side of mailbox storage. Lays out one Maildir per mailbox at <root>/<tenant-slug>/<local-part>@<domain>/ with the standard tmp/, new/, cur/ triple, and Maildir++ .Folder/ subdirectories for folders other than INBOX. Writes are crash-safe in the Maildir sense: the file is fully written and flushed under tmp/ and then atomically renamed into new/, so a reader never sees a partial message.

## Members

- **Delete** *(method)* - Delete a message file. Returns if a file was removed.
- **EnsureFolder** *(method)* - Ensure the Maildir for a mailbox folder exists (creating tmp/new/cur as needed). Returns the folder's Maildir directory.
- **Move** *(method)* - Move a message file to another folder of the same mailbox, keeping its file name and flags. Returns the new relative path, or if the source file does not exist.
- **ReadAsync** *(method)* - Read a message previously written. Returns if the file no longer exists.
- **SetFlags** *(method)* - Rename a message so its file name carries the given Maildir flags. A message in new/ moves to cur/; a message already in cur/ has its :2, suffix rewritten. Returns the new relative path, or if the file does not exist.
- **WriteAsync** *(method)* - Write a message into a folder's new/ directory.
- **Root** *(property)* - The root directory under which all tenants live.
