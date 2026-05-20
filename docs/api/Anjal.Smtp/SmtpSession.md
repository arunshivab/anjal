# SmtpSession

**Namespace:** `Anjal.Smtp`

One connected SMTP client. Implements the RFC 5321 command grammar for the subset of commands a non-relay server needs: HELO, EHLO, MAIL, RCPT, DATA, RSET, NOOP, QUIT, HELP, VRFY. Line endings are normalised to CRLF on output; input tolerates bare LF.

## Members

- **#ctor** *(method)* - Construct a session for an accepted TCP client.
- **#ctor** *(method)* - Construct a session for an accepted TCP client, with optional inbound authentication.
- **RunAsync** *(method)* - Run the session until the client quits or disconnects. Safe to call once per session.
