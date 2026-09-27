# EvidenceMaintenance

**Namespace:** `Anjal.Mailbox`

One-off corrections for mail stored before v1.0.0-rc.8 (SPEC-08 R-02, R-12). Both run as a dry run first. Neither changes a message file. Label recovery (DEF-065): how each message arrived, read back from the Received: line this server added to it - the exact TLS version and cipher since rc.7; ESMTPS (encrypted) or ESMTP before (RFC 3848). Reconstruction: an evidence copy of each stored message without one, with the lines this server added removed, marked reconstructed - never presented as received.

## Members

- **#ctor** *(method)* - Construct.
- **HeaderFields** *(method)* - The header fields at the top of a message: name, unfolded value, and the byte offset just past the field.
- **OwnReceived** *(method)* - _(no description)_
- **ReadOwnReceived** *(method)* - How a message arrived, from the first Received: line naming this server. Null when there is none (mail created on this server).
- **ReconstructEvidenceAsync** *(method)* - Make a reconstructed evidence copy of every stored message that has none.
- **RecoverTransportLabelsAsync** *(method)* - Recover every stored message's record of how it arrived.
- **RemoveOwnAdditions** *(method)* - The message without the lines this server added at the top - its Received line, its spam verdict and its Authentication-Results line - stopping at the first line it did not add.
