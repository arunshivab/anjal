# WordJob

**Namespace:** `Anjal.Viewer`

rc.29 (DES-22 §3.2, AC-06, AC-24): a Word document as readable text - headings and list levels from the style ids Word writes, tables, its pictures re-drawn as PNG, and each run's bold, italic, underline, strikethrough, raised or lowered text and capitals as Chuvadi.Docs 2.2.0 resolves them (DocxReader.FormattedParagraphs: defaults, styles, then the run's own - Anjal resolves no style itself). Read with the untrusted limits; Document.Load is never used. No fonts, sizes, colours or links.

## Members

- **MaxPictureBytes** *(field)* - At most this many bytes of pictures are given back.
- **MaxPictures** *(field)* - At most this many pictures are re-drawn and shown.
- **Block** *(method)* - A paragraph's block: a heading or a list item by its style id's name only (AC-06), else a paragraph; with its runs when any of them is formatted (AC-24).
- **Blocks** *(method)* - AC-24: the blocks in the document's order. The library gives the body in order with the paragraphs of table cells in place (each with its cell), then the headers and footers; a table becomes one block where its first paragraph is, and a table inside a cell becomes its own block after the cell it is in.
- **Run** *(method)* - Reads a Word document into blocks in the document's order, with its pictures.
- **Runs** *(method)* - AC-24: a paragraph's runs with the formatting the library resolved for each ([i] is [i]'s). Deleted (tracked) and hidden text, markers, pictures, text boxes and symbol characters are left out; neighbouring runs that look the same are joined.
