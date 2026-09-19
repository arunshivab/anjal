# FolderRow

**Namespace:** `Anjal.Store`

A folder inside a mailbox. INBOX maps to the Maildir root; every other folder maps to a .Name subdirectory (Maildir++ convention).

## Members

- **Inbox** *(field)* - The name of the inbox folder.
- **MaildirNameFor** *(method)* - The Maildir++ directory name for a folder: empty for INBOX (the Maildir root), otherwise .Name.
- **CreatedAt** *(property)* - When the folder was created.
- **Id** *(property)* - Identifier assigned by the store.
- **MailboxId** *(property)* - The owning mailbox.
- **Name** *(property)* - Folder name, case-sensitive as shown to users (INBOX, Sent, Drafts, Trash, ...).
