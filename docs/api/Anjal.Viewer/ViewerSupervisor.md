# ViewerSupervisor

**Namespace:** `Anjal.Viewer`

rc.29 (DES-22 §4.1, D-358): the viewer's main process. It listens on its socket, keeps warmed single-use child processes ready, shares the children fairly (four jobs a person, 32 waiting in all), gives each job to a fresh child, and stops a child at its job's deadline or when it passes 768 MB. It never reads a file itself.

## Members

- **#ctor** *(method)* - Creates the supervisor.
- **DisposeAsync** *(method)* - Stops listening and every child.
- **RunAsync** *(method)* - Serves until stopped.
- **Start** *(method)* - Starts listening and keeping children ready; returns once the socket is open.
- **Jobs** *(property)* - Jobs done since start.
- **KilledByDeadline** *(property)* - Children stopped at their deadline.
- **KilledByMemory** *(property)* - Children stopped for their memory.
