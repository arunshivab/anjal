# RateLimitOptions

**Namespace:** `Anjal.Spam`

Limits for . Zero disables a limit.

## Members

- **ConnectionsPerMinute** *(property)* - Connections per client IP per minute on this listener. Default 60.
- **MaxEntries** *(property)* - Most keys any one table remembers. Past it, expired keys are swept at once instead of waiting for the ten-minute sweep, and if that is not enough the least recently active keys are forgotten. A flood of distinct source addresses can then cost bounded memory, not the process. Default 100,000.
- **MessagesPerHourPerIp** *(property)* - Messages (MAIL FROM) per client IP per hour for unauthenticated sessions. Default 200.
- **MessagesPerHourPerUser** *(property)* - Messages (MAIL FROM) per authenticated user per hour. Default 100.
