# PolicyDecision

**Namespace:** `Anjal.Smtp`

Decision returned by an check.

## Members

- **Allow** *(field)* - A decision that lets the command proceed.
- **Defer** *(method)* - Build a temporary (4xx) refusal.
- **Reject** *(method)* - Build a permanent (5xx) refusal.
- **Allowed** *(property)* - Whether the command may proceed.
- **ReplyCode** *(property)* - SMTP reply code to send when not allowed. 4xx asks the client to retry later (rate limits, greylisting); 5xx refuses permanently.
- **ReplyText** *(property)* - Reply text to send when not allowed.
