# ViewerClient

**Namespace:** `Anjal.Viewing`

rc.29 (DES-22 §4): the webmail's side of the viewer. Each request opens the socket, sends the request and the file, and reads the answer. The viewer stops a child that runs past its job's deadline, so the wait here is that deadline plus the time a request may spend in the queue.

## Members

- **#ctor** *(method)* - Creates a client for the viewer listening at a socket.
- **AskAsync** *(method)* - Asks the viewer to do one job on a file.
- **SocketPath** *(property)* - The socket this client speaks to.
