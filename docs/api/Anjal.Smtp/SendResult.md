# SendResult

**Namespace:** `Anjal.Smtp`

Result of an outbound send attempt.

## Members

- **Message** *(property)* - Description of the result. Empty on plain success.
- **Outcome** *(property)* - The outcome classification.
- **ReplyCode** *(property)* - SMTP reply code from the remote server (250 on success), or 0 if no reply.
