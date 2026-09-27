# EvidenceRow

**Namespace:** `Anjal.Store`

One evidence copy (v1.0.0-rc.8, ANJAL-DES-01): the exact bytes of a message as received or sent, with its SHA-256. The file is written once and never changed.

## Members

- **In** *(field)* - Incoming mail.
- **Out** *(field)* - Outgoing mail.
- **AllCopiesDeletedAt** *(property)* - When the last mailbox copy was deleted, or null.
- **AuthenticatedUser** *(property)* - The submitting user, when authenticated.
- **CapturedAt** *(property)* - When the bytes were captured (UTC).
- **ClientHostName** *(property)* - The name the client greeted with (incoming).
- **Direction** *(property)* - or .
- **EnvelopeFrom** *(property)* - The envelope sender.
- **EnvelopeTo** *(property)* - The envelope recipients.
- **Id** *(property)* - The id; also the file name.
- **Outcome** *(property)* - pending, accepted, not-accepted or sent.
- **Path** *(property)* - Path under the evidence root, forward slashes.
- **PurgeAfter** *(property)* - When it may be purged, or null while copies exist.
- **PurgeReason** *(property)* - Why it was purged.
- **PurgedAt** *(property)* - When it was purged, or null.
- **Reconstructed** *(property)* - True for copies made later from stored mail, not captured as received.
- **RemoteAddress** *(property)* - The connecting client's address (incoming).
- **RetentionDays** *(property)* - Days kept after the last mailbox copy is deleted.
- **SentMessageId** *(property)* - For outgoing mail: the Sent copy it belongs to, or null (for example mail sent through the API).
- **Sha256** *(property)* - SHA-256 of the file, lower-case hex.
- **SizeBytes** *(property)* - Size of the evidence file in bytes.
- **TransportTls** *(property)* - TLS version and cipher of the connection, or null when unencrypted.
