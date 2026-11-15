# SheetView

**Namespace:** `Anjal.Viewing`

rc.29 (DES-22 §3.3, D-361): one sheet of a workbook, 2,000 rows at most, the real size stated.

## Members

- **Columns** *(property)* - How many columns the grid has.
- **Cut** *(property)* - True when the sheet has more rows than are drawn (2,000).
- **HiddenColumns** *(property)* - Hidden columns (1-based).
- **Index** *(property)* - The sheet shown (0-based).
- **Merges** *(property)* - Merged ranges ("A4:B4").
- **Notices** *(property)* - Notices to show above the grid.
- **Rows** *(property)* - The rows shown that hold anything.
- **Sheets** *(property)* - All the workbook's sheets, for the tabs.
- **Shown** *(property)* - The last row shown.
- **Total** *(property)* - The sheet's last row with anything in it, as the file says (0 when the file does not say).
- **Widths** *(property)* - Column widths in characters, from the first column; 0 for the sheet's default.
