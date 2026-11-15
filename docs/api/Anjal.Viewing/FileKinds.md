# FileKinds

**Namespace:** `Anjal.Viewing`

rc.29 (AC-17): which viewer opens a file, judged by its name and first bytes.

## Members

- **Archive** *(field)* - A ZIP archive.
- **Dicom** *(field)* - A DICOM file.
- **Pdf** *(field)* - A PDF.
- **Picture** *(field)* - A picture the browser draws (PNG, JPEG, GIF, WebP, BMP).
- **Sheet** *(field)* - A workbook (.xlsx).
- **Word** *(field)* - A Word document (.docx).
- **IsDicom** *(method)* - Whether a file's bytes carry the DICOM mark ("DICM" after a 128-byte preamble).
- **Of** *(method)* - The kind of a file by its name and declared type; null when no viewer opens it. DICOM files are recognised by their bytes ("DICM" at 128), not their name; call for those.
- **PictureType** *(method)* - The type of a picture by its first bytes; null when it is not one the browser is given.
