# IMaildirStore

**Namespace:** `Anjal.Mailbox`

Filesystem side of mailbox storage. Lays out one Maildir per mailbox at <root>/<tenant-slug>/<local-part>@<domain>/ with the standard tmp/, new/, cur/ triple, and Maildir++ .Folder/ subdirectories for folders other than INBOX. Writes are crash-safe in the Maildir sense: the file is fully written and flushed under tmp/ and then atomically renamed into new/, so a reader never sees a partial message.

## Members

- **EnsureFolder** *(method)* - Ensure the Maildir for a mailbox folder exists (creating tmp/new/cur as needed). Returns the folder's Maildir directory.
- **ReadAsync** *(method)* - Read a message previously written. Returns if the file no longer exists.
- **WriteAsync** *(method)* - Write a message into a folder's new/ directory.
- **Root** *(property)* - The root directory under which all tenants live.
