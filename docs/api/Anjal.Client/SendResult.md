# SendResult

**Namespace:** `Anjal.Client`

What a send was given (202).

## Members

- **Id** *(property)* - The send's id.
- **MessageId** *(property)* - The Message-ID Anjal gave the message.
- **Replayed** *(property)* - True when Anjal replayed the first answer to this Idempotency-Key.
- **Sandbox** *(property)* - True for a sandbox key's send.
- **Status** *(property)* - queued, delivered (all here) or suppressed.
- **Suppressed** *(property)* - Recipients held back by the do-not-send list.
