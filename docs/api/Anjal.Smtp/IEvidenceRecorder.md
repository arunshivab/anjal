# IEvidenceRecorder

**Namespace:** `Anjal.Smtp`

Keeps the original of every incoming message exactly as received (v1.0.0-rc.8, ANJAL-DES-01). The SMTP session calls it before delivery; if it cannot record, the message is deferred - nothing is accepted without its evidence (SPEC-08 R-08).

## Members

- **CompleteInboundAsync** *(method)* - Record whether the message was accepted; a copy of mail not accepted is purged.
- **RecordInboundAsync** *(method)* - Store the original; returns its evidence id. Throws when it cannot be stored.
