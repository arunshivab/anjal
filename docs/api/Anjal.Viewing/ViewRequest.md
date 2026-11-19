# ViewRequest

**Namespace:** `Anjal.Viewing`

rc.29 (DES-22 §4.2): one request to the viewer process. The file's bytes travel beside it; nothing about the person or the organisation does - is an opaque key, used only to share the viewer fairly.

## Members

- **ArchivePassword** *(property)* - AC-20: for a file inside an archive ( set), the password of the attachment itself - the archive - for this request only; never kept.
- **ContentType** *(property)* - The file's declared type.
- **Culture** *(property)* - The reader's language and region ("en-IN", "ta-IN"), for number and date formats.
- **Entry** *(property)* - A file inside an archive: the entry's place, and for an archive inside the archive the places one after another ("3", "3/1"). Null for the file itself.
- **InnerArchivePassword** *(property)* - AC-20: for a file inside an archive inside the archive ("3/5"), the password of that inner archive ("3"), for this request only; never kept.
- **Kind** *(property)* - What to do: one of the .
- **Name** *(property)* - The file's name as the mail gave it.
- **Page** *(property)* - A page (0-based) for a PDF page, its text, or the first page of a search.
- **Password** *(property)* - The file's password, for this request only; never kept.
- **Plan** *(property)* - rc.29 part 2 (DES-22 §7): the editor's changes, for and .
- **Query** *(property)* - The words to find.
- **Sheet** *(property)* - A sheet (0-based) of a workbook.
- **Switches** *(property)* - The editor's held-back parts the webmail allows (D-405, D-406); null allows none. Set by the webmail on its own, never taken from the page.
- **Who** *(property)* - An opaque key for the person asking, so one person cannot take the whole viewer.
- **Width** *(property)* - The width in pixels to draw a page at.
