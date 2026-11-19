# EditCheck

**Namespace:** `Anjal.Viewing`

What the editor must know before it opens (§7.8, 1; D-374).

## Members

- **Active** *(property)* - The file holds scripts, actions or embedded files (the result never does, D-368).
- **HasForm** *(property)* - The file has form fields (they become part of the page when its pages are rearranged or joined).
- **HasLayers** *(property)* - The file has hidden layers (optional content): its pages are not changed until chuvadi-pdf 3.35.1 (D-406).
- **Locked** *(property)* - The file has a password (the result will not, D-374).
- **PageCount** *(property)* - The number of pages.
- **Signatures** *(property)* - The file's digital signatures, each with who signed it and whether it holds.
