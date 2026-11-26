# CsvJob

**Namespace:** `Anjal.Viewer`

rc.31 (D4, the coordinator, 25 Nov 2026): a CSV or TSV file as a plain table, read in the viewer's child like every other file. Nothing in it is ever evaluated or run: every cell is shown as the text it holds, so a cell starting with =, +, - or @ is shown as written, never as a formula. Caps: 10 MB read, 5,000 rows, 200 columns and 1,000 characters shown a cell; past any cap the first part is shown with a plain note. The answer is a , so the table is drawn like a workbook's sheet.

## Members

- **MaxBytes** *(field)* - The most bytes read.
- **MaxCellChars** *(field)* - The most characters shown in one cell.
- **MaxColumns** *(field)* - The most columns shown.
- **MaxRows** *(field)* - The most rows shown.
- **Decode** *(method)* - UTF-8 (with or without its mark) when the bytes are valid UTF-8; otherwise Windows-1252.
- **Read** *(method)* - Reads a CSV or TSV file into a table (the work of , for tests).
- **Run** *(method)* - Reads a CSV or TSV file into a table.
