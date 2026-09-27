# SettingsHandler

**Namespace:** `Anjal.Api.Endpoints`

/api/settings (v1.0.0-rc.7): the non-secret settings of the server and the webmail, kept in the database so a rebuild from backup restores them. Every change is audited by the API; it takes effect when the service restarts. Secrets are refused.

## Members

- **#ctor** *(method)* - Construct with the backing store.
- **DeleteAsync** *(method)* - DELETE /api/settings/{scope}/{key} - remove a stored value.
- **ListAsync** *(method)* - GET /api/settings - both scopes.
- **PutAsync** *(method)* - PUT /api/settings/{scope}/{key} - store a value.
