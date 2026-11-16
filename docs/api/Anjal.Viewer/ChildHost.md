# ChildHost

**Namespace:** `Anjal.Viewer`

rc.29 (DES-22 §4.1): a child process - one job and then it exits. It warms the libraries, says it is ready (one byte, "R"), reads one request from its input, answers on its output, and is gone. Nothing a library might print can reach the answer: the console's text output goes nowhere, and the answer is written to the raw output stream. Every exception is caught (AC-11): the libraries' become statuses in their jobs; anything else becomes "failed", with the exception's type - never its message - on the error stream for the log.

## Members

- **LogPrefix** *(field)* - The prefix of the lines the supervisor passes on to the log; any other line from a child is dropped.
- **Ready** *(field)* - The byte a child writes when it is warm and ready for its job.
- **RunAsync** *(method)* - Runs the child.
