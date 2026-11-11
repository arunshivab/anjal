# ImapServerOptions

**Namespace:** `Anjal.Imap`

rc.28 (DES-20 §3): how the IMAP server listens.

## Members

- **AuthFailures** *(property)* - Failed sign-ins per address on the internet (shared with submission).
- **BindAddress** *(property)* - The address to listen on.
- **Certificate** *(property)* - The certificate, read for each connection (it is renewed while the server runs).
- **HostName** *(property)* - The host name in the greeting.
- **IdlePoll** *(property)* - How often IDLE looks for new mail.
- **IdleTimeout** *(property)* - A session ends after this long without a command (IDLE included).
- **ImplicitTls** *(property)* - TLS from the first byte (RFC 8314). Off only in tests.
- **Log** *(property)* - Optional log.
- **MaxAppendBytes** *(property)* - The largest message an app may APPEND.
- **MaxSessions** *(property)* - Sessions in all.
- **MaxSessionsPerUser** *(property)* - Sessions one person may have at once.
- **Port** *(property)* - The port (993); 0 picks a free one (tests).
