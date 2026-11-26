# ViewNotices

**Namespace:** `Anjal.Viewing`

rc.29 (DES-22 §2): the notices shown above a viewed file, as codes the webmail turns into words.

## Members

- **Active** *(field)* - A Word file with other active content (ActiveX, embedded objects, links to outside files).
- **Incomplete** *(field)* - The file was repaired while it was read: what is shown is what could be read.
- **Macros** *(field)* - A Word or Excel file with macros.
- **ManyPages** *(field)* - More pages than are shown (2,000).
- **Partial** *(field)* - Not every part could be shown (a picture the library cannot draw yet, a damaged page, a skipped part).
- **PdfActive** *(field)* - A PDF with scripts, actions or embedded files.
- **TableCut** *(field)* - rc.31 (D4): a table (CSV) is shown only in part: past 10 MB, 5,000 rows, 200 columns or 1,000 characters a cell.
- **Xfa** *(field)* - An XFA form, which draws little or nothing from its pages.
