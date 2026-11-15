# WordBlock

**Namespace:** `Anjal.Viewing`

One block of a Word document: a heading (K "h1"-"h6"), a paragraph ("p"), a list item ("li", with its level), a table ("table", with its rows) or a picture ("img", the picture's place among the answer's files, or -1 for a picture of a kind Anjal does not show).

## Members

- **Alt** *(property)* - A picture's own description, from the file.
- **Img** *(property)* - A picture's place among the answer's files; -1 when it is not shown.
- **K** *(property)* - The kind.
- **L** *(property)* - A list item's level (1 at the top).
- **R** *(property)* - AC-24: the text as runs with their formatting; null when the whole text is plain, and then is shown.
- **Rows** *(property)* - A table's rows.
- **RunRows** *(property)* - AC-24: a table's cells as runs, row by row; a cell is null when its text is plain.
- **T** *(property)* - The text.
