# OutboundCipher

**Namespace:** `Anjal.Store`

rc.25 (DES-15 §7.4, D-244): a message waiting in the outgoing queue. It must be readable when it is handed to the receiving server, so it is sealed to the mail server's seal key (the same key that locks the DKIM keys; only the mail server holds its private half) - the webmail and a copy of the database cannot read it. Beside it, a small record (From, To, Subject, Date, Message-ID) is sealed to whoever may see it: the sending person's mailbox key, or the organisation's recovery key for an application's mail - so Outbox and Sending systems still show what was sent, to the right people. When the message is delivered, the sealed message is removed and only the record stays. "ANJQ" | version 1 | record length (u32, big-endian) | record (envelope, or a plain header block) | queued message (envelope; absent once removed) The record comes first so that a list reading only the first 8 KB still has it.

## Members

- **ForReader** *(method)* - What a reader of the queue sees: the whole message in the mail server; elsewhere the record, opened with the keys of the current flow - or nothing when it cannot be opened here.
- **IsSealed** *(method)* - Whether stored queue bytes are a sealed container.
- **RecordOnly** *(method)* - The stored bytes once the message is delivered: the record only (null when not sealed).
- **Seal** *(method)* - The stored form of a message to queue.
- **ServerOpener** *(property)* - In the mail server only: opens a queued message with the server's private seal key. Unset in the webmail, which therefore sees only the record.
