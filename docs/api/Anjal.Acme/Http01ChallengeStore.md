# Http01ChallengeStore

**Namespace:** `Anjal.Acme`

Holds HTTP-01 key authorizations while a challenge is pending. The process that owns port 80 serves /.well-known/acme-challenge/{token} from this table. It is a process-wide singleton so any HTTP host (Kestrel or HttpListener) can answer without knowing which renewal is in flight.

## Members

- **PathPrefix** *(field)* - The URL path prefix ACME validators request.
- **Add** *(method)* - Register a token with its key authorization.
- **Lookup** *(method)* - Resolve a request path to a key authorization. Returns null when the path is not a challenge path or the token is unknown.
- **Remove** *(method)* - Remove a token after validation.
- **Count** *(property)* - Number of pending tokens (for status and tests).
