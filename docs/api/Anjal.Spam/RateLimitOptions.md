# RateLimitOptions

**Namespace:** `Anjal.Spam`

Limits for . Zero disables a limit.

## Members

- **ConnectionsPerMinute** *(property)* - Connections per client IP per minute on this listener. Default 60.
- **MessagesPerHourPerIp** *(property)* - Messages (MAIL FROM) per client IP per hour for unauthenticated sessions. Default 200.
- **MessagesPerHourPerUser** *(property)* - Messages (MAIL FROM) per authenticated user per hour. Default 100.
