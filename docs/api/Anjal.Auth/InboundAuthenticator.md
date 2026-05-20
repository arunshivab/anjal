# InboundAuthenticator

**Namespace:** `Anjal.Auth`

Orchestrates SPF + DKIM + DMARC verification for an inbound message. Designed to be invoked from SmtpSession after DATA, producing a structured for the routing pipeline.

## Members

- **#ctor** *(method)* - Construct.
- **AuthenticateAsync** *(method)* - Run all three checks and assemble an .
- **ExtractDomain** *(method)* - Extract the domain portion from a "user@domain" address.
- **ExtractFromDomain** *(method)* - Extract the domain from the message's From header.
