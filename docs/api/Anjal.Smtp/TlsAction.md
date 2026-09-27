# TlsAction

**Namespace:** `Anjal.Smtp`

What an outbound session does after EHLO.

## Members

- **Hold** *(field)* - Do not send; the message is held, retried and finally returned to its sender.
- **Plaintext** *(field)* - Send without encryption.
- **StartTls** *(field)* - Negotiate STARTTLS, then send.
