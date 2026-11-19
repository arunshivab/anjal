# PdfEdit

**Namespace:** `Anjal.Viewer`

rc.29 part 2 (DES-22 §7): the PDF editor's work in the viewer's child process. The original is only read; every result is a new file. Save applies the plan in one pass and in DES-22's order - arrange (with join's flattening), marks, signatures, watermark and page numbers (one overlay), then redaction (apply, verify, sanitize, no outline, the compressor last) or, when nothing was redacted, the sanitizer last - and then opens the result again and draws its first page. Every write runs inside (AC-11); the child's deadline and memory cap remain the real guarantee.

## Members

- **FindPadding** *(field)* - Pad each redaction box by this much on every side (DES-22 §7.6: the boxes come from font metrics).
- **MaxMarks** *(field)* - The most marks one plan may carry.
- **MaxPages** *(field)* - The most pages the result may have.
- **MaxPoints** *(field)* - The most points one mark may carry (a long signature or pen stroke).
- **MaxText** *(field)* - The longest text a text box, watermark or page-number line may hold.
- **Arrange** *(method)* - The pages in their new order and turn, as a PDF: the viewer's own jobs then show it, find in it and give its text, so the person edits the pages as they will be. Nothing else of the plan is applied.
- **Check** *(method)* - What the editor must know before it opens: signatures, a password, forms, active content (§7.8, 1).
- **DisplayToUser** *(method)* - The transform from the page as shown - its CropBox turned by its /Rotate, origin bottom-left, y up, in points, as each overlay page is drawn - to the page's user space, where PlaceMany and the Redactor work: chuvadi-pdf 3.34.0's own (GetDisplayTransform, D-335; L-02, L-13). Anjal's own counter-rotation of rc.29's design (DES-22 §7.4) is not built.
- **HasLayers** *(method)* - True when the file declares hidden layers (optional content, ISO 32000 §8.11).
- **RedactionRects** *(method)* - The redaction boxes in each page's user space, each padded as DES-22 §7.6 asks of found words.
- **Save** *(method)* - Applies the whole plan, checks the result, and gives back the new PDF with a report (§7.1, §7.8).
