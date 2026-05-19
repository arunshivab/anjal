# RawHeader

**Namespace:** `Anjal.Dkim`

One parsed header line: name and raw value (no colon, possibly containing internal CRLF for folded continuations).

## Members

- **#ctor** *(method)* - Construct.
- **Name** *(property)* - The header name as it appeared (original casing).
- **Value** *(property)* - The header value as it appeared (with leading WSP and any continuation lines preserved verbatim).
