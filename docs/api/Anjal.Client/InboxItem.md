# InboxItem

**Namespace:** `Anjal.Client`

One mail waiting in the application's inbox (DES-14 §14): no subject, which is inside the envelope.

## Members

- **Acknowledged** *(property)* - True once acknowledged.
- **Auth** *(property)* - The SPF, DKIM and DMARC results: {spf, dkim, dmarc}.
- **From** *(property)* - Who sent it.
- **Id** *(property)* - Its id.
- **InReplyToSend** *(property)* - The application's send it answers, or null.
- **ReceivedAt** *(property)* - When it arrived.
- **Size** *(property)* - Its size.
- **SpamScore** *(property)* - The spam score.
- **To** *(property)* - The address it came to.
