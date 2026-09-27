# EvidenceHandler

**Namespace:** `Anjal.Api.Endpoints`

v1.0.0-rc.8 (SPEC-08 R-02, R-03, R-12, R-13): the evidence store and the one-off corrections, for the Anjal operator. Every evidence read is audited here (the server audits only changes). Corrections are dry runs unless ?apply=true.

## Members

- **#ctor** *(method)* - Construct.
- **GetAsync** *(method)* - GET /api/evidence/{id}: details and delivery attempts.
- **GetRawAsync** *(method)* - GET /api/evidence/{id}/raw: the exact bytes, checked against the recorded SHA-256 first.
- **ListTrustedSendersAsync** *(method)* - GET /api/maintenance/trusted-senders: every mailbox's trusted senders.
- **ReconstructAsync** *(method)* - POST /api/maintenance/reconstruct-evidence[?apply=true]: evidence for mail stored before rc.8.
- **RecoverLabelsAsync** *(method)* - POST /api/maintenance/transport-labels[?apply=true]: recover how past mail arrived.
- **RemoveTrustedSenderAsync** *(method)* - DELETE /api/maintenance/trusted-senders?mailbox=..&sender=..: remove one, as the owner chooses.
- **VerifyChainAsync** *(method)* - POST /api/evidence/verify: the whole manifest chain and every surviving copy.
- **VerifyOneAsync** *(method)* - GET /api/evidence/{id}/verify: one copy against its recorded SHA-256.
