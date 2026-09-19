# Http01Listener

**Namespace:** `Anjal.Acme`

Minimal HTTP listener that answers HTTP-01 challenges from and nothing else. Used by a deployment that hosts renewal in Anjal.Server (no webmail) and therefore has no Kestrel on port 80. Every other path gets 404.

## Members

- **#ctor** *(method)* - Construct.
- **Dispose** *(method)* - _(no description)_
- **RunAsync** *(method)* - Serve until cancelled.
