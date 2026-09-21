# RateLimiter

**Namespace:** `Anjal.Spam`

In-memory sliding-window rate limiter. Counts are per process and reset on restart; that is deliberate for v1 - the goal is to blunt bursts from one source, not to keep a ledger. Windows are pruned lazily on access and idle keys are swept periodically.

## Members

- **#ctor** *(method)* - Construct.
- **Hit** *(method)* - Record one event and report whether the key is still within its limit.
- **OnConnect** *(method)* - Synchronous connect check.
- **OnConnectAsync** *(method)* - _(no description)_
- **OnMailFrom** *(method)* - Synchronous MAIL FROM check.
- **OnMailFromAsync** *(method)* - _(no description)_
- **OnRcptToAsync** *(method)* - _(no description)_
- **Shrink** *(method)* - Sweep now; if still at capacity, drop the least recently active tenth.
- **TrackedAddresses** *(property)* - Keys currently held across the limiter's tables (for metrics and tests).
