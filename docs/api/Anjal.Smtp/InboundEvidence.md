# InboundEvidence

**Namespace:** `Anjal.Smtp`

An incoming message exactly as received, with how it arrived.

## Members

- **AuthenticatedUser** *(property)* - The authenticated user on the submission port, or null.
- **ClientHostName** *(property)* - The name the client greeted with.
- **EnvelopeFrom** *(property)* - The envelope sender.
- **EnvelopeTo** *(property)* - The envelope recipients.
- **RawBytes** *(property)* - The bytes after SMTP framing (dot-stuffing) was removed and before any change.
- **ReceivedAt** *(property)* - When the message was received.
- **RemoteAddress** *(property)* - The client's address.
- **TransportTls** *(property)* - TLS version and cipher, or null.
