# Counters

**Namespace:** `Anjal.Smtp`

Process-wide counters and gauges for operational metrics, rendered by the API's /metrics endpoint in Prometheus text format. Lives in Anjal.Smtp because every other module already references it. Counters only go up; gauges are callbacks evaluated at scrape time. Names follow Prometheus conventions: anjal_<subsystem>_<thing>_total.

## Members

- **Add** *(method)* - Add to a counter.
- **Describe** *(method)* - Attach help text to a metric (shown as # HELP).
- **Get** *(method)* - Read a counter (zero if unknown).
- **Increment** *(method)* - Increment a counter by one (creating it at zero if new).
- **RegisterGauge** *(method)* - Register (or replace) a gauge evaluated at scrape time.
- **RenderPrometheus** *(method)* - Render everything in Prometheus text exposition format (version 0.0.4), sorted by name so output is stable.
- **UnregisterGauge** *(method)* - Remove a gauge (tests, or a component shutting down).
