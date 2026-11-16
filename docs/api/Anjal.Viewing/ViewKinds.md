# ViewKinds

**Namespace:** `Anjal.Viewing`

rc.29 (DES-22 §4): the jobs the viewer process does.

## Members

- **ArchiveEntry** *(field)* - One file from inside an archive, for its Download (never a program).
- **ArchiveList** *(field)* - What is inside an archive (AC-17).
- **Dicom** *(field)* - A DICOM file's preview (switched off until the Siaara package is on the feed, D-372).
- **PdfClean** *(field)* - A copy of a PDF without its scripts, actions and embedded files (D-367).
- **PdfFind** *(field)* - Words found in a PDF.
- **PdfInfo** *(field)* - A PDF's pages, sizes, outline and notices.
- **PdfPage** *(field)* - One PDF page drawn as a PNG.
- **PdfText** *(field)* - One PDF page's text and where it sits.
- **Picture** *(field)* - A picture checked by its bytes and given back as itself (inside an archive).
- **Sheet** *(field)* - One sheet of a workbook (2,000 rows at most, D-361).
- **Word** *(field)* - A Word document as text, headings, lists, tables and pictures (AC-06).
- **Deadline** *(method)* - How long the job may take before its child process is stopped (DES-22 §4.1): open and the first page 10 s, another page 5 s, a sheet 8 s, Word 15 s, DICOM 8 s, an editor save 60 s.
