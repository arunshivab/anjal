# SendResult

**Namespace:** `Anjal.Smtp`

Result of an outbound send attempt.

## Members

- **WithRoute** *(method)* - A copy that also names the server spoken to and the TLS used.
- **Message** *(property)* - Description of the result. Empty on plain success.
- **Outcome** *(property)* - The outcome classification.
- **RemoteHost** *(property)* - The server this attempt spoke to (v1.0.0-rc.8), or empty when none was reached.
- **ReplyCode** *(property)* - SMTP reply code from the remote server (250 on success), or 0 if no reply.
- **TransportTls** *(property)* - TLS version and cipher negotiated with it, or null when the attempt was not encrypted.
