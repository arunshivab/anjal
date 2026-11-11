# ImapText

**Namespace:** `Anjal.Imap`

rc.28 (DES-20): how IMAP writes strings, dates, folder names and sets of numbers.

## Members

- **Date** *(method)* - An INTERNALDATE ("09-Nov-2026 14:02:00 +0000").
- **DecodeFolder** *(method)* - A folder name from IMAP's modified UTF-7.
- **EncodeFolder** *(method)* - A folder name as IMAP writes it (modified UTF-7, RFC 3501 §5.1.3).
- **NString** *(method)* - NIL for empty, otherwise .
- **ParseDate** *(method)* - Read an IMAP date ("9-Nov-2026") or date-time ("09-Nov-2026 14:02:00 +0530").
- **ParseSet** *(method)* - Read a set of numbers ("1:5,7,9:*"); "*" is the largest. Ranges either way round; nothing outside 1.. is returned unless it was named directly.
- **Quote** *(method)* - A quoted string, or a literal when the text needs one (line breaks, 8-bit).
- **WriteSet** *(method)* - Numbers written as a compact set ("3:5,9").
