# ISmtpPolicy

**Namespace:** `Anjal.Smtp`

Connection-level and transaction-level policy consulted by before it acts on a command. Used for rate limiting and greylisting. Implementations must be thread-safe: one instance serves every concurrent session of a listener. Any exception thrown is treated as "allow" so a policy bug can never take the server down.

## Members

- **OnConnect** *(method)* - Called when a client connects, before the banner. A refusal closes the connection after sending the reply.
- **OnMailFrom** *(method)* - Called on MAIL FROM after syntax and authorization checks.
- **OnRcptTo** *(method)* - Called on RCPT TO after the relay check, once per recipient.
