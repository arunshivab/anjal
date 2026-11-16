# SheetJob

**Namespace:** `Anjal.Viewer`

rc.29 (DES-22 §3.3, D-361): one sheet of a workbook as a grid - the file's own number formats in the reader's language, merged cells, column widths and hidden columns - with 2,000 rows at most and the sheet's real size stated. Read with the streaming reader and the untrusted limits.

## Members

- **MaxColumns** *(field)* - The most columns drawn (a sheet can have 16,384; the rest are named in a notice).
- **MaxRows** *(field)* - The most rows drawn a sheet (the owner's number, D-361).
- **Run** *(method)* - Reads one sheet.
- **UsedEnd** *(method)* - The last column and row of a used range ("A1:K250" gives 11 and 250); (0, 0) when there is none.
