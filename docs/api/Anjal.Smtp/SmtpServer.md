# SmtpServer

**Namespace:** `Anjal.Smtp`

SMTP server: listens on a TCP socket and spawns an per accepted connection. Sessions run concurrently.

## Members

- **#ctor** *(method)* - Construct a server. must be called to begin accepting connections.
- **Dispose** *(method)* - _(no description)_
- **StartAsync** *(method)* - Start listening. Returns once the socket is bound and ready to accept. The accept loop runs in the background until signals or is called.
- **BoundPort** *(property)* - The actual port the server bound to. Useful when configured with port 0 (auto-assign) - read after returns.
