# FileRisks

**Namespace:** `Anjal.Mailbox`

rc.24 (item 93; owner, 25 Oct 2026: "Deliver with a warning"): encrypted archives and files with macros are delivered, and the reader is warned. The checks read only the file's own structure - a ZIP's directory, an Office file's parts, the headers of RAR and 7-Zip archives - and never open or run anything. A file that cannot be read is simply not warned about.

## Members

- **Inspect** *(method)* - What to warn about for one file.
