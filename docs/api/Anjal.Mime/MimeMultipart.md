# MimeMultipart

**Namespace:** `Anjal.Mime`

A multipart MIME entity (multipart/mixed, multipart/alternative, multipart/related etc.) that contains an ordered list of child entities. Children may themselves be multiparts, allowing nested structure.

## Members

- **#ctor** *(method)* - Construct an empty multipart.
- **Epilogue** *(property)* - The text that appears after the closing boundary marker. Same semantics as .
- **Parts** *(property)* - The child entities in declaration order.
- **Preamble** *(property)* - The text that appears before the first boundary marker. Per RFC 2046 section 5.1.1, recipients should ignore this; we preserve it for fidelity but it has no semantic meaning.
