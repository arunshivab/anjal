# HealthHandler

**Namespace:** `Anjal.Api.Endpoints`

GET /healthz (unauthenticated) and GET /metrics (authenticated). Health probes the store, the Maildir root and the TLS certificate and returns 200 when everything is ok, 503 when any component is down; degraded (e.g. a certificate close to expiry) keeps 200 so a monitor can alert on the body without treating the service as failed.

## Members

- **#ctor** *(method)* - Construct.
- **CheckAsync** *(method)* - Run every probe and build the report.
- **WriteHealthAsync** *(method)* - Write the health report.
- **WriteMetricsAsync** *(method)* - Write the Prometheus metrics page.
