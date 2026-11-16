# ArchiveView

**Namespace:** `Anjal.Viewing`

rc.29 (AC-17): what is inside an archive, read in the viewer process without unpacking it.

## Members

- **Entries** *(property)* - The entries.
- **Expanded** *(property)* - Its size unpacked.
- **Locked** *(property)* - AC-20: true when the archive is locked with a password and was opened with it - its files are shown here only, were not virus-scanned, and are never given out one by one (only the original archive downloads).
- **Refused** *(property)* - Why it is not listed (); null when listed.
