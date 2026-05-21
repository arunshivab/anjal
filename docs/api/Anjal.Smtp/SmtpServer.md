# SmtpServer

**Namespace:** `Anjal.Smtp`

SMTP server: listens on a TCP socket and spawns an per accepted connection. Sessions run concurrently.

## Members

- **#ctor** *(method)* - Construct a server. must be called to begin accepting connections.
- **#ctor** *(method)* - Construct a server with inbound authentication only (PR 8 ctor).
- **#ctor** *(method)* - Construct a server with full PR 9 feature set: inbound (SPF/DKIM/DMARC) authentication, optional submission-side (AUTH PLAIN/LOGIN) authentication, and optional local-domain resolution for the open-relay guard.
- **Dispose** *(method)* - _(no description)_
- **StartAsync** *(method)* - Start listening. Returns once the socket is bound and ready to accept. The accept loop runs in the background until signals or is called.
- **BoundPort** *(property)* - The actual port the server bound to. Useful when configured with port 0 (auto-assign) - read after returns.
