# DeliveryContext

**Namespace:** `Anjal.Smtp`

Context for a delivery attempt. Captures the SMTP envelope and the fully-received DATA payload. The receiver hands one of these to its configured after each successful DATA.

## Members

- **ClientHostName** *(property)* - The EHLO/HELO hostname the client claimed.
- **EnvelopeFrom** *(property)* - The MAIL FROM address from the SMTP envelope (without angle brackets).
- **EnvelopeTo** *(property)* - The RCPT TO addresses from the SMTP envelope (without angle brackets).
- **RawBytes** *(property)* - The full DATA payload as received over the wire (CRLF preserved, dot-unstuffed).
- **RemoteAddress** *(property)* - IP address of the connected client, in dotted form.
