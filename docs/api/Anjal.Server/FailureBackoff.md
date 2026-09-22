# FailureBackoff

**Namespace:** `Anjal.Server`

For a polling loop that keeps failing - typically because the database is unreachable. The first failure is logged with its cause; the ones after it are not, and the loop waits longer each time (doubling from the normal interval, up to a ceiling). When a pass succeeds again, that is logged once. A one-hour outage used to write hundreds of identical lines, burying the one that mattered (DEF-026).

## Members

- **#ctor** *(method)* - Construct.
- **Failed** *(method)* - A pass failed. Returns the wait before the next one.
- **Succeeded** *(method)* - A pass succeeded. Returns the wait before the next one.
- **ConsecutiveFailures** *(property)* - Failures since the last success.
