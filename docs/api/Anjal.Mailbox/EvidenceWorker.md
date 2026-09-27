# EvidenceWorker

**Namespace:** `Anjal.Mailbox`

The evidence store's housekeeping (v1.0.0-rc.8, ANJAL-DES-01), run every hour. Every step is safe to repeat: writes a manifest for each complete UTC day not yet covered, chained to the previous one, re-hashing that day's files and recording any file that is missing, altered or has no database row (nothing is ever deleted to tidy up); starts the retention clock of outgoing evidence with no mailbox copy, a day after sending; purges copies whose retention has ended, recording each in a purge list for the backup; warns when the disk holding the evidence is more than 80% full.

## Members

- **Genesis** *(field)* - The previous hash of the first manifest.
- **#ctor** *(method)* - Construct.
- **AppendPurgeListAsync** *(method)* - The backup reads these lists to remove its own copy of purged files (append-only otherwise).
- **RunOnceAsync** *(method)* - One pass of every housekeeping step.
- **VerifyAsync** *(method)* - Check the whole chain: each manifest file against its recorded hash and its predecessor, and every copy a manifest lists that has not been purged since against its recorded hash.
