# RequestContext

**Namespace:** `Anjal.Api`

Wraps an with conveniences for reading the body and writing JSON responses.

## Members

- **#ctor** *(method)* - Construct with the underlying HttpListener context.
- **Query** *(method)* - Read a query-string parameter by name (case-insensitive). Returns if absent.
- **ReadBodyAsync** *(method)* - Read the request body as a string. Caches the result so repeated calls are free. Returns empty for GET / DELETE / requests with no body.
- **WriteEmptyAsync** *(method)* - Write an empty response with the given status code (used for 204 etc.).
- **WriteErrorAsync** *(method)* - Write a standard error response with status code and JSON body.
- **WriteJsonAsync** *(method)* - Write a JSON body and an HTTP status code, then close the response.
- **AuthorizationHeader** *(property)* - The Authorization header value, or empty if not set.
- **Method** *(property)* - HTTP method (GET, POST, etc.) in uppercase.
- **Path** *(property)* - Absolute path of the URL (e.g. "/api/routing-rules").
