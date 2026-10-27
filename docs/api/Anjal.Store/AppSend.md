# AppSend

**Namespace:** `Anjal.Store`

rc.26 (DES-14 §28.1): one send through the application API - one message, however many recipients. Its status is gathered from its rows in the outbound queue. No subject is kept (DES-14 §28.2): the application knows its own subjects.

## Members

- **AppId** *(property)* - The application.
- **BodySha256** *(property)* - The SHA-256 of the request body, hex.
- **CreatedAt** *(property)* - When it was received.
- **EvidenceId** *(property)* - The evidence copy of the message as built (DEF-076), or null (sandbox, refused).
- **From** *(property)* - The From address.
- **Id** *(property)* - Its id.
- **IdempotencyKey** *(property)* - The Idempotency-Key, or null (cleared after 24 hours).
- **LastEvent** *(property)* - The last event's type.
- **MessageId** *(property)* - The Message-ID Anjal gave it, without angle brackets.
- **Metadata** *(property)* - The application's metadata.
- **Response** *(property)* - The response first given (JSON), replayed for the same key and body.
- **Sandbox** *(property)* - True for a sandbox key's send: nothing leaves.
- **ScheduledAt** *(property)* - When it is to be sent, or null for at once.
- **Status** *(property)* - "processing" while it is checked, then queued, sending, delivered, deferred, bounced, failed, suppressed or sandbox.
- **Tags** *(property)* - The application's tags.
- **Template** *(property)* - The template used, or empty.
- **TemplateVersion** *(property)* - The template's version used.
- **TenantId** *(property)* - The organisation.
- **To** *(property)* - Every recipient (To, Cc and Bcc), lower case.
