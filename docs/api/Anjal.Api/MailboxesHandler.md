# MailboxesHandler

**Namespace:** `Anjal.Api.Endpoints`

Endpoint handlers for /api/mailboxes and /api/messages. Passwords are accepted as plaintext in POST bodies, hashed via PBKDF2 before persisting, and NEVER returned. Creating a mailbox also lays out its Maildir with the four default folders (INBOX, Sent, Drafts, Trash) so the directory exists before the first delivery.

## Members

- **DefaultFolders** *(field)* - Folders created for every new mailbox.
- **#ctor** *(method)* - Construct.
- **DeleteAsync** *(method)* - DELETE /api/mailboxes/{address} - remove the mailbox and its index rows (Maildir files stay on disk).
- **GetAsync** *(method)* - GET /api/mailboxes/{address} - fetch one.
- **GetMessageAsync** *(method)* - GET /api/messages/{id} - one message's metadata.
- **GetMessageRawAsync** *(method)* - GET /api/messages/{id}/raw - the full message bytes, base64 encoded.
- **ListAsync** *(method)* - GET /api/mailboxes[?tenant=slug] - list.
- **ListFoldersAsync** *(method)* - GET /api/mailboxes/{address}/folders - list folders with counts.
- **ListMessagesAsync** *(method)* - GET /api/mailboxes/{address}/messages[?folder=INBOX&limit=50&offset=0] - page message metadata, newest first.
- **PostAsync** *(method)* - POST /api/mailboxes - create or update.
