# InboundMessage

**Namespace:** `Anjal.Store`

A stored inbound message. The is assigned by the store on save; callers should ignore the value they pass in.

## Members

- **EnvelopeFrom** *(property)* - SMTP envelope MAIL FROM. May differ from the Message header From.
- **EnvelopeTo** *(property)* - SMTP envelope RCPT TO. Always exactly one address per stored row.
- **Id** *(property)* - Identifier assigned by the store. if not yet persisted.
- **LocalPart** *(property)* - Resolved local-part (left of "@", with any "+tag" stripped).
- **MessageId** *(property)* - The Message-ID header value with angle brackets stripped, or empty.
- **RawBytes** *(property)* - The raw RFC 5322 message bytes as received over SMTP.
- **ReceivedAt** *(property)* - Time the message was received and stored.
- **Subject** *(property)* - The Subject header, decoded if RFC 2047 encoded.
- **Tag** *(property)* - The "+tag" portion of the recipient if present, otherwise empty.
