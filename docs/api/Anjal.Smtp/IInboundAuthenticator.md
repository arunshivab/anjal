# IInboundAuthenticator

**Namespace:** `Anjal.Smtp`

Optional pluggable authenticator for inbound mail. Runs after DATA but before the message is handed to the sink. Lets the server enforce SPF/DKIM/DMARC verdicts at the SMTP layer (e.g. reject 550 when DMARC says p=reject).

## Members

- **AuthenticateAsync** *(method)* - Authenticate an inbound message. Returns a structured result containing per-verifier verdicts and the formatted Authentication-Results header value. The implementation MUST NOT throw - it should return TempError/PermError verdicts instead of raising.
