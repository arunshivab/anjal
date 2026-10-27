# MessageStatus

**Namespace:** `Anjal.Client`

A message's status (DES-14 §8). The subject is not kept: the application knows its own.

## Members

- **Attempts** *(property)* - Each recipient's state, and every delivery attempt with the receiving server's reply.
- **From** *(property)* - From.
- **Id** *(property)* - The send's id.
- **LastEvent** *(property)* - The last event's type.
- **MessageId** *(property)* - The Message-ID.
- **Metadata** *(property)* - The metadata.
- **Raw** *(property)* - The whole answer, for fields this package does not name.
- **Status** *(property)* - queued, sending, delivered, deferred, bounced, failed, suppressed.
- **Tags** *(property)* - The tags.
- **To** *(property)* - Every recipient.
