# SmtpServerOptions

**Namespace:** `Anjal.Smtp`

Configuration for an instance.

## Members

- **CurrentTlsCertificate** *(method)* - The certificate to use for a session starting now: the result if a source is set, otherwise .
- **AdvertisedHostName** *(property)* - The hostname this server announces in 220 banners and EHLO responses.
- **AllowPlaintextAuth** *(property)* - For , allow AUTH commands on plaintext connections (no STARTTLS). DEFAULT IS FALSE. Enable ONLY for local development or controlled networks - AUTH without TLS leaks credentials. The submission port refuses authentication unless this is true or TLS is active.
- **AuthFailures** *(property)* - Shared per-address AUTH failure counter for this listener. Null disables the cross-session limit (the per-session one still applies).
- **BindAddress** *(property)* - IP address to bind. Defaults to loopback for local development.
- **CommandTimeout** *(property)* - Idle-timeout per command. Connections that go this long with no data are closed.
- **ImplicitTls** *(property)* - Implicit TLS (RFC 8314): the TLS handshake happens as soon as the client connects, before the banner, as on port 465. No plaintext is ever exchanged, so there is no STARTTLS to strip. A connection whose handshake fails is closed without a word.
- **Log** *(property)* - Optional diagnostic log for errors a session recovers from but should not hide.
- **MaxAuthFailuresPerSession** *(property)* - Failed AUTH attempts allowed in one session before it is closed. Default 3.
- **MaxConcurrentSessions** *(property)* - Concurrent sessions this listener serves at once. Past this, new connections get 421 4.7.0 and are closed before a session is created. Default 200.
- **MaxMessageBytes** *(property)* - Maximum DATA size in bytes. Default 25 MB matches Gmail/Outlook for now.
- **MaxRecipients** *(property)* - Maximum number of RCPT TO recipients per transaction. RFC 5321 minimum is 100.
- **MaxSessionDuration** *(property)* - The longest a single session may last, however active. Stops a client that drips one byte just inside from holding a connection forever. Generous enough for a 25 MB message over a slow link. Default 15 minutes.
- **MaxSessionsPerAddress** *(property)* - Concurrent sessions from one client address. Large senders open a few connections in parallel; ten leaves room for that. Default 10.
- **Policy** *(property)* - Optional connection/transaction policy (rate limiting, greylisting). Null means every connection and command is allowed.
- **Port** *(property)* - TCP port to listen on. Default 2525 to avoid needing admin on dev machines.
- **Recipients** *(property)* - Optional check for whether a recipient on one of our own domains exists, so an unknown one is refused at RCPT TO instead of after the message is transferred (DEF-042). Null leaves the decision to delivery.
- **RequireTlsForMail** *(property)* - When true, the server refuses MAIL FROM, RCPT TO, and DATA on plaintext connections after EHLO - clients must STARTTLS first. Has no effect if is null. Defaults to false (TLS opportunistic on receiver side).
- **Role** *(property)* - The role this listener plays. Determines which commands are accepted and which authorization checks apply. Defaults to which is correct for a public port 25 listener.
- **TlsCertificate** *(property)* - X.509 certificate (with private key) used for STARTTLS. When set, the server advertises STARTTLS in EHLO and accepts upgrades. When null, STARTTLS is not advertised and the server runs plaintext only.
- **TlsCertificateSource** *(property)* - Optional live certificate source, consulted at the start of every session. When set it takes precedence over , so a renewed certificate is used by new connections without restarting the server. Returning null means "no certificate yet" and disables STARTTLS for that session.
