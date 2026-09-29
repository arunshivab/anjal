# DeliveryContext

**Namespace:** `Anjal.Smtp`

Context for a delivery attempt. Captures the SMTP envelope and the fully-received DATA payload. The receiver hands one of these to its configured after each successful DATA.

## Members

- **#ctor** *(method)* - A new, empty delivery context.
- **#ctor** *(method)* - The one copy of every property (v1.0.0-rc.9, DEF-077). A property added to this class is added here, and every With- method keeps it; the guard test fails otherwise. Hand-written copies lost TransportTls once (DEF-065) and the evidence link once (DEF-077).
- **WithRawBytes** *(method)* - A copy with every property kept and only the message bytes replaced - for a sink that rewrites the message (for example to add headers) before passing it on (DEF-065).
- **WithRecipients** *(method)* - A copy with every property kept and only the recipients replaced (v1.0.0-rc.9, DEF-077).
- **AuthResults** *(property)* - Authentication detail (SPF/DKIM/DMARC verdicts) if an authenticator was configured. Null if inbound auth is disabled. The concrete type is Anjal.Auth.AuthenticationResults when populated.
- **AuthenticatedUser** *(property)* - Username that authenticated on the submission port, or null for unauthenticated (MTA) deliveries. Authenticated mail is never scored for spam.
- **ClientHostName** *(property)* - The EHLO/HELO hostname the client claimed.
- **EnvelopeFrom** *(property)* - The MAIL FROM address from the SMTP envelope (without angle brackets).
- **EnvelopeTo** *(property)* - The RCPT TO addresses from the SMTP envelope (without angle brackets).
- **EvidenceId** *(property)* - The evidence copy of this message as received (v1.0.0-rc.8), or null when none is kept.
- **RawBytes** *(property)* - The full DATA payload as received over the wire (CRLF preserved, dot-unstuffed).
- **RemoteAddress** *(property)* - IP address of the connected client, in dotted form.
- **TransportTls** *(property)* - The TLS version and cipher suite the sending server used to reach this server (for example TLSv1.3 TLS_AES_256_GCM_SHA384), or null when the message arrived unencrypted.
