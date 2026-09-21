# FolderRow

**Namespace:** `Anjal.Store`

A folder inside a mailbox. INBOX maps to the Maildir root; every other folder maps to a .Name subdirectory (Maildir++ convention).

## Members

- **Inbox** *(field)* - The name of the inbox folder.
- **MaxNameLength** *(field)* - The longest folder name accepted.
- **IsValidName** *(method)* - Whether a name is acceptable for a folder: 1-64 printable ASCII characters, no path separators, not starting with a dot. The store refuses anything else, so no caller can create a folder whose name would mean something to the filesystem.
- **MaildirNameFor** *(method)* - The Maildir++ directory name for a folder: empty for INBOX (the Maildir root), otherwise .Name.
- **CreatedAt** *(property)* - When the folder was created.
- **Id** *(property)* - Identifier assigned by the store.
- **MailboxId** *(property)* - The owning mailbox.
- **Name** *(property)* - Folder name, case-sensitive as shown to users (INBOX, Sent, Drafts, Trash, ...).
