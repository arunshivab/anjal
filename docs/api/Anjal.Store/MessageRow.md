# MessageRow

**Namespace:** `Anjal.Store`

Metadata for one message stored in a mailbox folder. The message body lives only in the Maildir file named by ; the store row exists for listing, searching and flagging without touching the filesystem.

## Members

- **Answered** *(property)* - Maildir "R" flag - the message has been replied to.
- **CategoryId** *(property)* - The category this message carries, or null.
- **DateHeader** *(property)* - The raw Date header value, or empty.
- **EnvelopeFrom** *(property)* - SMTP envelope MAIL FROM.
- **Flagged** *(property)* - Maildir "F" flag - the message is flagged/starred.
- **FolderId** *(property)* - The folder the message is in.
- **FromHeader** *(property)* - The raw From header value.
- **HasAttachments** *(property)* - True when the message has at least one attachment part.
- **Id** *(property)* - Identifier assigned by the store.
- **MailboxId** *(property)* - The owning mailbox.
- **MaildirFile** *(property)* - Path of the Maildir file relative to the folder's Maildir directory, e.g. new/1726560000.M123456P4242Q7.host.
- **MessageId** *(property)* - The Message-ID header value with angle brackets stripped, or empty.
- **ReceivedAt** *(property)* - Time the message was delivered to the folder.
- **Seen** *(property)* - Maildir "S" flag - the message has been read.
- **SizeBytes** *(property)* - Size of the Maildir file in bytes.
- **SpamScore** *(property)* - Spam score assigned at delivery (0 when scoring was not run).
- **Subject** *(property)* - The Subject header, decoded if RFC 2047 encoded.
- **ToHeader** *(property)* - The raw To header value.
