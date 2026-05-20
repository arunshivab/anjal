# WebhookPayload

**Namespace:** `Anjal.Routing`

Payload sent to a webhook subscriber. The receiver should validate the signature using the shared secret before processing.

## Members

- **AuthResultsJson** *(property)* - Inbound authentication results (SPF/DKIM/DMARC) as a JSON object fragment (without surrounding braces, e.g. "spf":{...},"dkim":{...},"dmarc":{...}). Empty when inbound authentication is disabled. When populated, the payload's authResults key embeds this fragment. Produced by Anjal.Auth.AuthResultsJson.Serialize.
- **CorrelationKey** *(property)* - Correlation key from the matched tag grant, or empty.
- **EnvelopeFrom** *(property)* - The SMTP envelope sender.
- **InboundMessageId** *(property)* - Identifier of the persisted inbound message.
- **LocalPart** *(property)* - The matched local-part of the routing rule.
- **MessageId** *(property)* - The Message-ID with angle brackets stripped.
- **RawBytesBase64** *(property)* - The raw RFC 5322 bytes of the message, base64-encoded.
- **ReceivedAt** *(property)* - Time the message was received.
- **Recipient** *(property)* - Lowercased recipient address as received over SMTP.
- **Subject** *(property)* - The decoded Subject header.
- **Tag** *(property)* - The "+tag" if present, otherwise empty.
