# AuthFailureLimiter

**Namespace:** `Anjal.Smtp`

Counts failed SMTP AUTH attempts per client address over a sliding window, shared by every session of a listener. Once an address passes the limit, further AUTH attempts are refused with a temporary failure before the password is checked at all - which also stops the ~50 ms of PBKDF2 per guess from becoming a CPU lever.

## Members

- **#ctor** *(method)* - Construct.
- **IsAllowed** *(method)* - Whether this address may attempt AUTH now.
- **RecordFailure** *(method)* - Record one failed attempt.
- **MaxFailures** *(property)* - Failures allowed per address within .
- **TrackedAddresses** *(property)* - Addresses currently tracked (for tests and metrics).
- **Window** *(property)* - The sliding window.
