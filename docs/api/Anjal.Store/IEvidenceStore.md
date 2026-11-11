# IEvidenceStore

**Namespace:** `Anjal.Store`

The database side of the evidence store (v1.0.0-rc.8, ANJAL-DES-01).

## Members

- **AddEvidenceAttemptAsync** *(method)* - Record an outgoing delivery attempt.
- **GetEvidenceAsync** *(method)* - One copy, or null.
- **GetLatestEvidenceManifestAsync** *(method)* - The most recent manifest, or null before the first.
- **GetOutboundEvidenceLinkAsync** *(method)* - A queued row's evidence copy and Sent copy, when known.
- **InsertEvidenceAsync** *(method)* - Record an evidence copy whose file is already safely on disk.
- **InsertEvidenceManifestAsync** *(method)* - Record a day's manifest.
- **LinkOutboundToSentCopyAsync** *(method)* - Link queued rows (and any evidence already made for them) to the Sent copy saved for them.
- **ListEvidenceAttemptsAsync** *(method)* - An outgoing copy's attempts, oldest first.
- **ListEvidenceCapturedAsync** *(method)* - Copies captured in [start, end), in capture order.
- **ListEvidenceDueAsync** *(method)* - Copies due for purging at , oldest first.
- **ListEvidenceManifestsAsync** *(method)* - Every manifest, oldest first.
- **ListEvidencePurgedAsync** *(method)* - Copies purged in [start, end), in purge order.
- **MarkEvidencePurgedAsync** *(method)* - Mark a copy purged (its file has been removed).
- **RaiseEvidenceRetentionAsync** *(method)* - Raise a copy's retention to at least (the longest of the tenants it reached).
- **ResealEvidenceAsync** *(method)* - rc.28 (D-264, DES-18 §11): an original kept before rc.25 now sealed to its organisation - its new file, size and SHA-256, keeping the SHA-256 it had as so the manifest of its day still proves it. Done once: an original already re-sealed is left alone.
- **SetEvidenceOutcomeAsync** *(method)* - Set an incoming copy's outcome; not-accepted copies become due for purging at once.
- **SetMessageEvidenceAsync** *(method)* - Link a stored message to its evidence copy (only when it has none yet).
- **SetOutboundEvidenceAsync** *(method)* - Record the evidence copy made for a queued row (signed once; every retry sends it).
- **StartClocksForOutboundWithoutMailboxCopyAsync** *(method)* - Start the retention clock, from its capture, of outgoing evidence that has no mailbox copy (for example mail sent through the API) and was captured before .
- **UpdateMessageTransportAsync** *(method)* - Correct a stored message's record of how it arrived (label recovery; the message file is not touched).
