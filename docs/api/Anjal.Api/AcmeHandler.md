# AcmeHandler

**Namespace:** `Anjal.Api.Endpoints`

Endpoint handlers for /api/acme. The API process is not necessarily the one running renewal, so status is read from the store's status.json and certificate files, and a renewal is requested by dropping the marker the renewal service polls for.

## Members

- **#ctor** *(method)* - Construct.
- **GetStatusAsync** *(method)* - GET /api/acme - certificate and renewal status.
- **RenewAsync** *(method)* - POST /api/acme/renew - request an immediate renewal.
