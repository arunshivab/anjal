# SmtpSession

**Namespace:** `Anjal.Smtp`

One connected SMTP client. Implements the RFC 5321 command grammar for the subset of commands a non-relay server needs: HELO, EHLO, MAIL, RCPT, DATA, RSET, NOOP, QUIT, HELP, VRFY. Line endings are normalised to CRLF on output; input tolerates bare LF.

## Members

- **#ctor** *(method)* - Construct a session for an accepted TCP client.
- **#ctor** *(method)* - Construct a session with inbound authentication (SPF/DKIM/DMARC) but no submission-side AUTH. Retained for backward compatibility with PR 8 callers.
- **#ctor** *(method)* - Construct a session with full feature set: inbound auth, submission auth, and local-domain resolution. This is the constructor used by in PR 9 and later.
- **CompleteAuthAsync** *(method)* - Common path for both AUTH PLAIN and AUTH LOGIN: hand credentials to the authenticator and reply 235 (success) or 535 (failure). On success the user is bound to this session for subsequent MAIL FROM authorization.
- **Consult** *(method)* - Ask the configured policy. A missing policy, or one that throws, allows the command - a policy bug must never take the server down.
- **ExtractDomain** *(method)* - Extract the domain portion from a "user@domain" address. Returns empty if not a valid local@domain form.
- **HandleAuthAsync** *(method)* - Handle AUTH PLAIN and AUTH LOGIN per RFC 4954. AUTH is only valid after EHLO on submission listeners with an authenticator configured. Strict TLS-before-AUTH unless options.AllowPlaintextAuth.
- **HandleAuthLoginAsync** *(method)* - AUTH LOGIN: a Microsoft-originated mechanism widely deployed. The server prompts "Username:" (base64), client sends base64(username), server prompts "Password:" (base64), client sends base64(password). The initial response (if any) is the base64-encoded username.
- **HandleAuthPlainAsync** *(method)* - AUTH PLAIN flow per RFC 4616: the credentials are base64-encoded "[authzid] NUL authcid NUL password". If no initial response is provided, prompt with "334" and read on the next line.
- **RunAsync** *(method)* - Run the session until the client quits or disconnects. Safe to call once per session.
