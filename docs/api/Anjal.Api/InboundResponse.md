# InboundResponse

**Namespace:** `Anjal.Api.Dto`

Response body for GET /api/inbound/{id}.

## Members

- **EnvelopeFrom** *(property)* - SMTP envelope sender.
- **EnvelopeTo** *(property)* - SMTP envelope recipient.
- **Id** *(property)* - The message identifier.
- **LocalPart** *(property)* - Decoded local-part.
- **MessageId** *(property)* - Message-ID header.
- **RawBytesBase64** *(property)* - Raw MIME bytes, base64-encoded.
- **ReceivedAt** *(property)* - When the message was received.
- **Subject** *(property)* - Decoded Subject header.
- **Tag** *(property)* - Decoded tag.
