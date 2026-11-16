# ArchiveRefusals

**Namespace:** `Anjal.Viewing`

rc.29 (AC-17): why an archive is not listed.

## Members

- **TooDeep** *(field)* - An archive inside an archive deeper than two levels.
- **TooLarge** *(field)* - Larger unpacked than allowed (200 MB).
- **TooMany** *(field)* - More entries than are listed (2,000).
- **TooPacked** *(field)* - Packed more tightly than an ordinary archive (100:1).
- **Unreadable** *(field)* - Not a readable ZIP.
- **Unsupported** *(field)* - AC-20: an archive made in a way Anjal cannot open (a split archive, or a method it does not unpack).
