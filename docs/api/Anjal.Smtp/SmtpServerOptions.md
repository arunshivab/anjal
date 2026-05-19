# SmtpServerOptions

**Namespace:** `Anjal.Smtp`

Configuration for an instance.

## Members

- **AdvertisedHostName** *(property)* - The hostname this server announces in 220 banners and EHLO responses.
- **BindAddress** *(property)* - IP address to bind. Defaults to loopback for local development.
- **CommandTimeout** *(property)* - Idle-timeout per command. Connections that go this long with no data are closed.
- **MaxMessageBytes** *(property)* - Maximum DATA size in bytes. Default 25 MB matches Gmail/Outlook for now.
- **MaxRecipients** *(property)* - Maximum number of RCPT TO recipients per transaction. RFC 5321 minimum is 100.
- **Port** *(property)* - TCP port to listen on. Default 2525 to avoid needing admin on dev machines.
