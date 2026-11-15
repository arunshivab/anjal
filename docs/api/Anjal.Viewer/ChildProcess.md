# ChildProcess

**Namespace:** `Anjal.Viewer`

rc.29 (DES-22 §4.1): one single-use child process. It is started and warmed before it is needed, given one job, watched - killed when its deadline passes or its memory passes the limit - and never used again.

## Members

- **Dispose** *(method)* - _(no description)_
- **Kill** *(method)* - Stops the child, if it is still there.
- **RunAsync** *(method)* - Gives the child its one job and waits for the answer, stopping it at the deadline or the memory limit.
- **StartAsync** *(method)* - Starts a child and waits until it says it is ready; null when it does not start or say so in time.
- **Id** *(property)* - The process's id.
