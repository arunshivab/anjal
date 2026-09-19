# RateLimiter

**Namespace:** `Anjal.Spam`

In-memory sliding-window rate limiter. Counts are per process and reset on restart; that is deliberate for v1 - the goal is to blunt bursts from one source, not to keep a ledger. Windows are pruned lazily on access and idle keys are swept periodically.

## Members

- **#ctor** *(method)* - Construct.
- **Hit** *(method)* - Record one event and report whether the key is still within its limit.
- **OnConnect** *(method)* - _(no description)_
- **OnMailFrom** *(method)* - _(no description)_
- **OnRcptTo** *(method)* - _(no description)_
