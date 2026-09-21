# SmtpSession

**Namespace:** `Anjal.Smtp`

One connected SMTP client. Implements the RFC 5321 command grammar for the subset of commands a non-relay server needs: HELO, EHLO, MAIL, RCPT, DATA, RSET, NOOP, QUIT, HELP, VRFY. Line endings are normalised to CRLF on output; input tolerates bare LF.

## Members

- **#ctor** *(method)* - Construct a session for an accepted TCP client.
- **#ctor** *(method)* - Construct a session with inbound authentication (SPF/DKIM/DMARC) but no submission-side AUTH. Retained for backward compatibility with PR 8 callers.
- **#ctor** *(method)* - Construct a session with full feature set: inbound auth, submission auth, and local-domain resolution. This is the constructor used by in PR 9 and later.
- **BeginImplicitTlsAsync** *(method)* - Wrap the connection in TLS before anything is said (port 465). The handshake itself is bounded by the idle timeout, so a client that connects and never speaks TLS cannot hold the slot.
- **CompleteAuthAsync** *(method)* - Common path for both AUTH PLAIN and AUTH LOGIN: hand credentials to the authenticator and reply 235 (success) or 535 (failure). On success the user is bound to this session for subsequent MAIL FROM authorization.
- **ConsultAsync** *(method)* - Ask the configured policy. A missing policy, or one that throws, allows the command - a policy bug must never take the server down.
- **DeclaredSize** *(method)* - The SIZE= value on a MAIL FROM line, or 0 when absent or unreadable.
- **ExtractDomain** *(method)* - Extract the domain portion from a "user@domain" address. Returns empty if not a valid local@domain form.
- **HandleAuthAsync** *(method)* - Handle AUTH PLAIN and AUTH LOGIN per RFC 4954. AUTH is only valid after EHLO on submission listeners with an authenticator configured. Strict TLS-before-AUTH unless options.AllowPlaintextAuth.
- **HandleAuthLoginAsync** *(method)* - AUTH LOGIN: a Microsoft-originated mechanism widely deployed. The server prompts "Username:" (base64), client sends base64(username), server prompts "Password:" (base64), client sends base64(password). The initial response (if any) is the base64-encoded username.
- **HandleAuthPlainAsync** *(method)* - AUTH PLAIN flow per RFC 4616: the credentials are base64-encoded "[authzid] NUL authcid NUL password". If no initial response is provided, prompt with "334" and read on the next line.
- **Inspect** *(method)* - During DATA, watch the raw bytes for a single "." standing on a line of its own where either line break is not CRLF: LF.LF, CR.CR, LF.CRLF and the like. A compliant client dot-stuffs every such line, so this only ever comes from an attempt to smuggle a second message past a lenient server, or from a client that would otherwise hang waiting for a terminator Anjal will never accept. Either way the message is refused. The real terminator, CRLF.CRLF, passes.
- **NormaliseLineEndings** *(method)* - Rewrite every bare CR and bare LF as CRLF, leaving existing CRLF pairs alone. Applied to a completed DATA body, after the terminator has already been found strictly, so it cannot change where the message ended.
- **ReadByteAsync** *(method)* - Next byte from the buffered input, refilling it as needed. Each refill waits at most ; a client that sends nothing for that long ends the session with 421, which is what stops a slowloris from holding connections open indefinitely.
- **ReadDataBodyAsync** *(method)* - Read a DATA body up to the end-of-data marker. Only CRLF.CRLF ends the body. A dot line ended by a bare LF or a bare CR does not, which is what keeps Anjal from being a receiver that SMTP smuggling (CVE-2023-51764 class) can split. Once the body is complete, any bare CR or LF left inside it is rewritten as CRLF, so the stored message - and anything later relayed from it - carries no ambiguous line endings for a lenient downstream server to misread. When the body passes the rest is read and discarded up to the marker, so the remainder is never parsed as commands; the caller then replies 552. A body that runs past twice the limit without ending is treated as abuse and the connection is dropped.
- **ReadLineAsync** *(method)* - Read one command line. A line longer than the RFC 5321 limit is read to its end and discarded, and is set so the caller replies 500 - the overflow is never mistaken for the next command.
- **ReceivedHeader** *(method)* - The trace header for this message: Received: from helo (ip) by host with ESMTPS id x; date. The protocol word follows RFC 3848: ESMTP, plus S when TLS was in use and A when the client authenticated.
- **RunAsync** *(method)* - Run the session until the client quits or disconnects. Safe to call once per session.
- **SanitiseTraceToken** *(method)* - The client's HELO name is untrusted text going into a header; keep only characters that can appear in a host name or address literal.
- **TryWriteFinalAsync** *(method)* - Best-effort final reply on a session that is closing anyway.
