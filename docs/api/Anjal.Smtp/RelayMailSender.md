# RelayMailSender

**Namespace:** `Anjal.Smtp`

Outbound sender that always relays through a single configured host. This is the implementation used when port 25 is blocked outbound (e.g. Azure, AWS, most Indian VPS providers) and the host is an authenticated SMTP relay service like SES, Mailgun, or Brevo. Note: TLS and SMTP AUTH are NOT yet implemented in v0.3.0. This sender will work against an unauthenticated test relay on the local machine (the end-to-end example uses Anjal's own receiver as the test relay) but not against production providers until Phase 2.

## Members

- **#ctor** *(method)* - Construct the relay sender with options.
- **ClassifyReply** *(method)* - Classify an SMTP reply code as transient (4xx) or permanent (5xx). Exposed as static for use by other senders.
- **SendAsync** *(method)* - _(no description)_
