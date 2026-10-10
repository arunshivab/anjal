# PwnedRefreshStatus

**Namespace:** `Anjal.Smtp`

Where a refresh of the leaked-password list stands (rc.15, item 35): written by the server beside the list as "<list>.status", read by the Operations console.

## Members

- **PathFor** *(method)* - The status file of a list file.
- **Read** *(method)* - Read the status beside a list file, or null when there is none or it cannot be read.
- **Write** *(method)* - Write this status beside a list file (replacing the old one whole).
- **Error** *(property)* - Why the last download failed, in a sentence.
- **Finished** *(property)* - When the last download ended.
- **Percent** *(property)* - How far a download has got, 0 to 100.
- **Started** *(property)* - When the last download started.
- **State** *(property)* - "building", "ready" or "failed".
