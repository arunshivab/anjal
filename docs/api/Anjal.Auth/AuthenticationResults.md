# AuthenticationResults

**Namespace:** `Anjal.Auth`

Aggregated authentication results for one inbound message. Produced by and exposed via the webhook payload.

## Members

- **Dkim** *(property)* - DKIM result and detail.
- **Dmarc** *(property)* - DMARC result and detail.
- **HeaderValue** *(property)* - The full RFC 8601 Authentication-Results header value to prepend to the message (without the field name or terminating CRLF).
- **ServingHost** *(property)* - The hostname of the verifying Anjal instance (used as the authserv-id in the Authentication-Results header).
- **Spf** *(property)* - SPF result and detail.
