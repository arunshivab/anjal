# ArchiveReader

**Namespace:** `Anjal.Viewer`

rc.28 (DES-18 §10, D-337), moved into the viewer process in rc.29 (AC-17, D-381) and read with Chuvadi.Zip (AC-20): what is inside a ZIP, read without unpacking it, and one entry unpacked in memory - never to disk - when the person opens or downloads it. The whole archive: 2,000 entries, 200 MB unpacked, 100:1 - on the sizes it declares, before anything is unpacked; and the library's own caps (, lowered to these) on the bytes actually produced. Each entry it gives out: exactly its own declared size, no more than 64 MB (a viewer's limit), and 100:1 on its own once it is larger than 1 MB. An archive inside the archive is listed as the second level, sharing the outer archive's budgets; a third level is a plain entry. Programs - by name, and by their first bytes - are never given out or opened. A locked archive (WinZip AES, or the old ZipCrypto) is opened only with its password, here only: its files are shown, never given out one by one (AC-20). Names are read as UTF-8 when the entry says so or the bytes are valid UTF-8, otherwise in the DOS code page (437) that older Windows tools write.

## Members

- **EntryRatioFrom** *(field)* - An entry larger than this must also stay within on its own (AC-17).
- **MaxEntries** *(field)* - At most this many entries are listed (D-337), the outer and inner archive together.
- **MaxEntryBytes** *(field)* - The most an entry may weigh to be opened or given out (a viewer's limit, DES-22 §5).
- **MaxExpanded** *(field)* - At most this many bytes unpacked, the outer and inner archive together (D-337).
- **MaxLevels** *(field)* - How many archives deep a listing goes (AC-17): the attachment, and one inside it.
- **MaxRatio** *(field)* - At most this many times larger unpacked than packed (D-337).
- **Entry** *(method)* - One file from inside an archive, unpacked in memory, for a viewer or its Download: never a program, a folder, an entry from a refused archive, or more than its own declared size or 64 MB. A file from a locked archive is given, marked , only for a viewer.
- **Limits** *(method)* - The library's caps for an archive Anjal reads: its preset for untrusted input, each lowered to Anjal's own budget where Anjal's is tighter (AC-20).
- **List** *(method)* - Lists an archive, or the archive inside it that names.
- **ParsePath** *(method)* - Reads "3" or "3/5" into places; an empty array for null or anything else.
- **Unpacked** *(method)* - rc.30 (AC-19): one listed entry of the archive itself (not one inside an archive in it), unpacked in memory with the same caps as - for the DICOM grouping, which reads the listing once and then each file in turn, holding one at a time.
