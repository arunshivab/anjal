# ApiServer

**Namespace:** `Anjal.Api`

HTTP API server. Wraps , applies bearer authentication, and dispatches by HTTP method + path to the appropriate endpoint handler.

## Members

- **#ctor** *(method)* - Construct an API server.
- **Dispose** *(method)* - _(no description)_
- **StartAsync** *(method)* - Start the accept loop. Returns a task that completes when the loop exits.
- **BoundPort** *(property)* - The actual port the server bound to. Useful when configured with port 0. Read after returns.
