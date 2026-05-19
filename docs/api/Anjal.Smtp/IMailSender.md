# IMailSender

**Namespace:** `Anjal.Smtp`

Sends outbound mail. Two implementations are provided: - looks up the destination domain's MX records and connects directly. - sends every message to a single configured upstream SMTP relay (useful when port 25 is blocked).

## Members

- **SendAsync** *(method)* - Attempt to deliver one message. The delivery context's list should normally contain recipients in a single destination domain - the caller groups by domain before invoking this method.
