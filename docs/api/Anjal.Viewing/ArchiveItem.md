# ArchiveItem

**Namespace:** `Anjal.Viewing`

One entry of an archive.

## Members

- **Blocked** *(property)* - True for a program: never offered, never opened.
- **Depth** *(property)* - How many folders deep.
- **Folder** *(property)* - True for a folder.
- **Index** *(property)* - Its place in the archive; negative for a folder that has no entry of its own, only files under it.
- **Modified** *(property)* - When it was last changed.
- **Name** *(property)* - Its own name.
- **Opens** *(property)* - The viewer that opens it (): "pdf", "word", "sheet", "picture", "archive", "dicom"; null when it is only a Download.
- **Path** *(property)* - Its full path inside.
- **Size** *(property)* - Its size unpacked.
- **Unsupported** *(property)* - AC-20: true when the entry uses a method Anjal cannot unpack (BZip2, LZMA, Deflate64, PPMd, strong encryption).
