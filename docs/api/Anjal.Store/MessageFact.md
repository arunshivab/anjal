# MessageFact

**Namespace:** `Anjal.Store`

What the dashboard and Today need to know about one message, without its body (rc.16, design review sections 7 and 8): its folder, who it was from and to, when it arrived, and the marks the figures count.

## Members

- **#ctor** *(method)* - What the dashboard and Today need to know about one message, without its body (rc.16, design review sections 7 and 8): its folder, who it was from and to, when it arrived, and the marks the figures count.
- **Answered** *(property)* - Answered.
- **CategoryId** *(property)* - Its category, if any.
- **EnvelopeFrom** *(property)* - The envelope sender.
- **Folder** *(property)* - Its folder's name.
- **FromHeader** *(property)* - The From header as stored.
- **HasAttachments** *(property)* - Carries attachments.
- **Id** *(property)* - The message.
- **ReceivedAt** *(property)* - When it arrived (or was sent).
- **Seen** *(property)* - Read.
- **SizeBytes** *(property)* - Its size.
- **SpamChecked** *(property)* - Whether it went through the incoming checks.
- **SpamScore** *(property)* - The spam score given on arrival.
- **Subject** *(property)* - The subject.
- **ToHeader** *(property)* - The To header as stored.
- **TransportEncrypted** *(property)* - Whether it arrived encrypted (null for own mail).
