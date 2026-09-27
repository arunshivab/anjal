# EvidenceAttemptRow

**Namespace:** `Anjal.Store`

One outgoing delivery attempt and the receiving server's reply.

## Members

- **AttemptedAt** *(property)* - When the attempt ended.
- **EvidenceId** *(property)* - The evidence copy of the message.
- **Outcome** *(property)* - delivered, deferred or refused.
- **Recipient** *(property)* - The recipient.
- **RemoteHost** *(property)* - The receiving host.
- **ReplyCode** *(property)* - The reply code, or null when there was none (connection failure).
- **ReplyText** *(property)* - The reply text, or the reason there was none.
- **TransportTls** *(property)* - TLS version and cipher, or null.
