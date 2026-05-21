# SmtpServerRole

**Namespace:** `Anjal.Smtp`

The role an SMTP listener plays. Affects which commands are accepted and which authorization checks apply.

## Members

- **Mta** *(field)* - Public-facing port 25 listener for inter-server mail delivery (MTA role). Accepts mail without authentication, but only for local domains (refuses relay). Does not advertise AUTH.
- **Submission** *(field)* - Submission port (typically 587) for authenticated client submission per RFC 6409. Requires AUTH before MAIL FROM (after STARTTLS unless explicitly opted into plaintext). Accepts mail for any destination.
