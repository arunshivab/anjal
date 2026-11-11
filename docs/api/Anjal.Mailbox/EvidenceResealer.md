# EvidenceResealer

**Namespace:** `Anjal.Mailbox`

rc.28 (D-264, DES-18 §11; the owner, 26 Oct: "re encrypt later"): the one-time job that seals what was kept before organisation-held encryption (rc.25) - the originals in the evidence store, and any mail still waiting in the outgoing queue. An organisation's original is sealed to its recovery key, as every original has been since rc.25; an individual's is left as it is (D-234). The day's manifest is never rewritten: the evidence record keeps the SHA-256 the manifest lists, a dated re-seal list beside the manifests records each change, and the plain file goes the way of a purged one (the backup's purge list removes its copy too). A dry run only counts. A second run finds nothing to do.

## Members

- **#ctor** *(method)* - Construct.
- **RunAsync** *(method)* - Seal what was kept before rc.25.
- **Clock** *(property)* - The clock (tests set it).
