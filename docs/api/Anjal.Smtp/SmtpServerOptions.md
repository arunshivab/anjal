# SmtpServerOptions

**Namespace:** `Anjal.Smtp`

Configuration for an instance.

## Members

- **AdvertisedHostName** *(property)* - The hostname this server announces in 220 banners and EHLO responses.
- **AllowPlaintextAuth** *(property)* - For , allow AUTH commands on plaintext connections (no STARTTLS). DEFAULT IS FALSE. Enable ONLY for local development or controlled networks - AUTH without TLS leaks credentials. The submission port refuses authentication unless this is true or TLS is active.
- **BindAddress** *(property)* - IP address to bind. Defaults to loopback for local development.
- **CommandTimeout** *(property)* - Idle-timeout per command. Connections that go this long with no data are closed.
- **MaxMessageBytes** *(property)* - Maximum DATA size in bytes. Default 25 MB matches Gmail/Outlook for now.
- **MaxRecipients** *(property)* - Maximum number of RCPT TO recipients per transaction. RFC 5321 minimum is 100.
- **Policy** *(property)* - Optional connection/transaction policy (rate limiting, greylisting). Null means every connection and command is allowed.
- **Port** *(property)* - TCP port to listen on. Default 2525 to avoid needing admin on dev machines.
- **RequireTlsForMail** *(property)* - When true, the server refuses MAIL FROM, RCPT TO, and DATA on plaintext connections after EHLO - clients must STARTTLS first. Has no effect if is null. Defaults to false (TLS opportunistic on receiver side).
- **Role** *(property)* - The role this listener plays. Determines which commands are accepted and which authorization checks apply. Defaults to which is correct for a public port 25 listener.
- **TlsCertificate** *(property)* - X.509 certificate (with private key) used for STARTTLS. When set, the server advertises STARTTLS in EHLO and accepts upgrades. When null, STARTTLS is not advertised and the server runs plaintext only.
