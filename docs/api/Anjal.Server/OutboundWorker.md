# OutboundWorker

**Namespace:** `Anjal.Server`

Background worker that drains the outbound queue. Leases batches of pending messages, hands each to an , then writes the result back to the store with exponential backoff for transient failures. Retry schedule: 1m, 5m, 15m, 1h, 6h, 24h. Permanent failures (5xx) and messages past their give_up_at deadline are marked Failed.

## Members

- **#ctor** *(method)* - Construct an outbound worker.
- **DrainOnceAsync** *(method)* - Run a single iteration: lease one batch, process every message in it, then return. Exposed so tests and demos can drive the worker manually without waiting on the polling interval.
- **RunAsync** *(method)* - Run the worker loop until is signalled. Each iteration: lease a batch, send each, mark each, sleep .
