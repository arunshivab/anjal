# ApiServer

**Namespace:** `Anjal.Api`

HTTP API server. Wraps , applies bearer authentication, and dispatches by HTTP method + path to the appropriate endpoint handler.

## Members

- **#ctor** *(method)* - Construct an API server.
- **#ctor** *(method)* - Construct an API server with mailbox endpoints enabled.
- **AuditAsync** *(method)* - Record every request that changes something. Method, path, outcome and client address only - never the body, which carries passwords, DKIM private keys and webhook secrets. A failure to record is logged, not fatal: the change has already been made.
- **Dispose** *(method)* - _(no description)_
- **StartAsync** *(method)* - Start the accept loop. Returns a task that completes when the loop exits.
- **TryDispatchMailboxAsync** *(method)* - Mailbox routes (v0.9.0). Returns if the path is not a mailbox route so the caller can fall through to 404.
- **BoundPort** *(property)* - The actual port the server bound to. Useful when configured with port 0. Read after returns.
