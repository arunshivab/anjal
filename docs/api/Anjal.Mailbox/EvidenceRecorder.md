# EvidenceRecorder

**Namespace:** `Anjal.Mailbox`

Keeps incoming originals (v1.0.0-rc.8): the file first, safely on disk, then its database row. If either fails, the exception reaches the SMTP session, which defers the message.

## Members

- **#ctor** *(method)* - Construct.
- **CompleteInboundAsync** *(method)* - _(no description)_
- **GetOutboundLinkAsync** *(method)* - A queued row's existing evidence copy and Sent copy, when known.
- **ReadVerifiedAsync** *(method)* - The exact bytes of an evidence copy, checked against its recorded SHA-256.
- **RecordAttemptAsync** *(method)* - Record one delivery attempt and the receiving server's reply; a delivered copy is marked sent.
- **RecordInboundAsync** *(method)* - _(no description)_
- **RecordOutboundAsync** *(method)* - Keep the exact outgoing bytes (after DKIM signing) of a queued row; every retry then sends them.
