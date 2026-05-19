# DkimKeysHandler

**Namespace:** `Anjal.Api.Endpoints`

Endpoint handlers for /api/dkim-keys. Private keys are accepted in request bodies but NEVER returned in responses.

## Members

- **#ctor** *(method)* - Construct.
- **DeleteAsync** *(method)* - DELETE /api/dkim-keys/{domain} - remove.
- **ListAsync** *(method)* - GET /api/dkim-keys - list metadata (never returns private keys).
- **PostAsync** *(method)* - POST /api/dkim-keys - upload or rotate.
