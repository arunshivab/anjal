# SubmissionHeaders

**Namespace:** `Anjal.Server`

Header completion for submitted mail (v1.0.0-rc.9, DEF-073; RFC 6409 8.2 and 8.3): a message a user's mail program submits without a Date or Message-ID gets one, added at the end of its header so the trace lines stay first.

## Members

- **Complete** *(method)* - The message with a Date and a Message-ID, adding whichever is missing.
