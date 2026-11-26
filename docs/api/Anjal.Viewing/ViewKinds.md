# ViewKinds

**Namespace:** `Anjal.Viewing`

rc.29 (DES-22 §4): the jobs the viewer process does.

## Members

- **ArchiveEntry** *(field)* - One file from inside an archive, for its Download (never a program).
- **ArchiveList** *(field)* - What is inside an archive (AC-17).
- **Csv** *(field)* - rc.31 (D4): a CSV or TSV file as a plain table (10 MB, 5,000 rows, 200 columns, 1,000 characters a cell).
- **Dicom** *(field)* - A DICOM file's preview (rc.30: SIAARA v0.1.0-rc.1; shown only with ANJAL_VIEWER_DICOM=on, D-372).
- **DicomGroup** *(field)* - rc.30 (AC-19, D-386): a ZIP's DICOM files grouped by patient, study and series. rc.31 (lite DICOM): run in a child of its own like every DICOM job (a 256 MiB heap, stopped past 320 MB), one grouping at a time.
- **DicomPatient** *(field)* - rc.30 (D-366): a DICOM file's patient name and ID, for Show patient details only.
- **DmarcReport** *(field)* - A DMARC aggregate report attached by a receiver (rc.15, items 31 to 33), unpacked and read here like every other outside file (D-405, decision A of 17 Nov 2026).
- **PdfArrange** *(field)* - rc.29 part 2 (DES-22 §7.7): the pages in their new order and turn, as a PDF the viewer's jobs can show.
- **PdfClean** *(field)* - A copy of a PDF without its scripts, actions and embedded files (D-367).
- **PdfEditCheck** *(field)* - rc.29 part 2 (DES-22 §7.8, 1): what the editor must know first - signatures, a password, forms.
- **PdfFind** *(field)* - Words found in a PDF.
- **PdfInfo** *(field)* - A PDF's pages, sizes, outline and notices.
- **PdfPage** *(field)* - One PDF page drawn as a PNG.
- **PdfSave** *(field)* - rc.29 part 2 (DES-22 §7.1): every change applied in one pass, checked, and the new PDF given back.
- **PdfSigns** *(field)* - rc.29 part 2 (DES-22 §7.6): the patient signs in a PDF's text, each with its boxes, to redact or not.
- **PdfText** *(field)* - One PDF page's text and where it sits.
- **Picture** *(field)* - A picture checked by its bytes and given back as itself (inside an archive).
- **Sheet** *(field)* - One sheet of a workbook (2,000 rows at most, D-361).
- **TlsReport** *(field)* - An SMTP TLS report attached by a receiver (RFC 8460; rc.15, item 59), read here (D-405, decision A).
- **Word** *(field)* - A Word document as text, headings, lists, tables and pictures (AC-06).
- **Deadline** *(method)* - How long the job may take before its child process is stopped (DES-22 §4.1): open and the first page 10 s, another page 5 s, a sheet 8 s, Word 15 s, DICOM 8 s, an editor save 60 s.
- **IsDicom** *(method)* - rc.31 (lite DICOM): whether a kind is a DICOM job, run in a child of its own at 320 MB.
