# SmtpClientSession

**Namespace:** `Anjal.Smtp`

Low-level SMTP client that connects to a single host:port and plays the client side of an RFC 5321 transaction. Designed to be driven by a higher level sender (, ) that decides which host to talk to.

## Members

- **ConnectAsync** *(method)* - Connect to : and read the greeting line. Throws on connection failure or non-220 greeting.
- **DataAsync** *(method)* - Send DATA, the message body with dot-stuffing applied, and the terminator. Returns the final reply.
- **Dispose** *(method)* - _(no description)_
- **DotStuff** *(method)* - Apply dot-stuffing per RFC 5321 section 4.5.2. Exposed as static for test use.
- **EhloAsync** *(method)* - Send EHLO <hostname> and read the multi-line reply.
- **EhloSupportsStartTls** *(method)* - Inspect a multi-line EHLO response to see whether the server advertises the STARTTLS capability.
- **MailFromAsync** *(method)* - Send MAIL FROM:<addr>.
- **QuitAsync** *(method)* - Send QUIT and read the 221 reply. Errors are swallowed - the caller always continues to dispose.
- **RcptToAsync** *(method)* - Send RCPT TO:<addr>.
- **StartTlsAsync** *(method)* - Issue STARTTLS and, on a 220 reply, upgrade the underlying stream to TLS. The caller MUST re-issue EHLO after this returns successfully, per RFC 3207.
- **IsTls** *(property)* - True after a successful STARTTLS handshake.
