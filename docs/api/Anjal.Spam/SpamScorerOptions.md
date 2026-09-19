# SpamScorerOptions

**Namespace:** `Anjal.Spam`

Tunable weights and lists for .

## Members

- **DkimFail** *(property)* - Points for a DKIM signature that fails verification.
- **DkimNone** *(property)* - Points when the message carries no DKIM signature.
- **DmarcFail** *(property)* - Points when DMARC evaluates to fail (whatever the published policy).
- **FromEnvelopeMismatch** *(property)* - Points when the From header domain differs from the envelope sender domain.
- **HeloMalformed** *(property)* - Points when HELO is a bare IP address or has no dot.
- **HeloUnresolvable** *(property)* - Points when HELO is well-formed but does not resolve.
- **ManyRecipients** *(property)* - Points for an envelope with many recipients.
- **ManyRecipientsThreshold** *(property)* - Recipient count above which applies.
- **MissingDate** *(property)* - Points when Date is missing.
- **MissingFrom** *(property)* - Points when the From header is missing or empty.
- **MissingMessageId** *(property)* - Points when Message-ID is missing.
- **NoReverseDns** *(property)* - Points when the connecting IP has no reverse DNS.
- **PhraseMaxPoints** *(property)* - Cap on phrase points per message.
- **PhrasePoints** *(property)* - Points per matched phrase, capped by .
- **Phrases** *(property)* - Phrases (case-insensitive substring match on subject and text body) that add each. Intentionally short and editable; a real corpus-trained filter is a later release.
- **SenderDomainUnreachable** *(property)* - Points when the sender domain cannot receive mail (no MX and no A/AAAA).
- **SpfFail** *(property)* - Points for SPF fail.
- **SpfNone** *(property)* - Points when the sender domain publishes no SPF record.
- **SpfSoftFail** *(property)* - Points for SPF softfail.
- **SubjectAllCaps** *(property)* - Points when the subject is entirely upper-case (8+ letters).
