# OutboundWorker

**Namespace:** `Anjal.Server`

Background worker that drains the outbound queue. Leases batches of pending messages, hands each to an , then writes the result back to the store with exponential backoff for transient failures. Retry schedule: 1m, 5m, 15m, 1h, 6h, 24h. Permanent failures (5xx) and messages past their give_up_at deadline are marked Failed.

## Members

- **#ctor** *(method)* - Construct an outbound worker.
- **DrainOnceAsync** *(method)* - Run a single iteration: lease one batch, process every message in it, then return. Exposed so tests and demos can drive the worker manually without waiting on the polling interval.
- **ExtractFromDomain** *(method)* - Extract the domain from the message's From: header. Returns null if no From header is found or its value has no @-domain.
- **NotifyFinalFailureAsync** *(method)* - Tell the sender, without letting a failure to tell them disturb the queue.
- **Route** *(method)* - " via host (TLS)" for a log line (v1.0.0-rc.8, INC-01 P-5): which server answered and whether the connection was encrypted; empty when no server was reached.
- **RunAsync** *(method)* - Run the worker loop until is signalled. Each iteration: lease a batch, send each, mark each, sleep .
