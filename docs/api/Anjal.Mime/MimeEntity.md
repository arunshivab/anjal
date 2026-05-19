# MimeEntity

**Namespace:** `Anjal.Mime`

Abstract base for any MIME entity - a part with headers and a body of some sort. Concrete subclasses are (leaf body) and (container of nested entities).

## Members

- **#ctor** *(method)* - Construct an entity with an empty header collection.
- **ContentTransferEncoding** *(property)* - The parsed Content-Transfer-Encoding for this entity. Defaults to if absent.
- **ContentType** *(property)* - The parsed Content-Type for this entity. If the header is absent or unparseable, returns .
- **Headers** *(property)* - The header fields of this entity, in order.
