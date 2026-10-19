# FileJudgement

**Namespace:** `Anjal.Mailbox`

The result of judging a message's files.

## Members

- **#ctor** *(method)* - The result of judging a message's files.
- **Detail** *(property)* - The virus name, or why the scanner was unavailable.
- **Files** *(property)* - The files concerned, by name.
- **RawBytes** *(property)* - The message to deliver (rebuilt when a file was removed), or null to deliver the original.
- **Scanned** *(property)* - How many files were scanned.
- **Verdict** *(property)* - What to do.
