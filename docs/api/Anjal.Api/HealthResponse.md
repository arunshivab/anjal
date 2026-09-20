# HealthResponse

**Namespace:** `Anjal.Api.Dto`

Response body for GET /healthz.

## Members

- **CheckedAt** *(property)* - When the probes ran (UTC).
- **Components** *(property)* - Per-component results.
- **Status** *(property)* - Worst component status: ok, degraded or down.
- **UptimeSeconds** *(property)* - Seconds since the API server started.
- **Version** *(property)* - API module version.
