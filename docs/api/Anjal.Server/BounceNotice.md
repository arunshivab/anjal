# BounceNotice

**Namespace:** `Anjal.Server`

Builds the delivery-failure notice a sender receives when an outbound message could not be delivered: an RFC 3464 multipart/report with a readable explanation, a machine-readable delivery-status part, and the original message's headers (not its body, which the sender already has). The notice is filed directly into the sender's own INBOX. It is never sent outbound: a bounce addressed to an arbitrary envelope sender is how servers become sources of backscatter, and only local mailboxes can submit mail here anyway.

## Members

- **MaxOriginalHeaderBytes** *(field)* - Headers of the original kept in the notice, at most this many bytes.
- **Build** *(method)* - Build the notice.
- **OneLine** *(method)* - Reduce text to one line for a header or diagnostic field.
