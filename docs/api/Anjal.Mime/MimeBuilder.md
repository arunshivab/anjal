# MimeBuilder

**Namespace:** `Anjal.Mime`

Serialises a back to wire-format bytes. Always emits CRLF line endings. Long header lines are folded at 78 columns where possible (RFC 5322 section 2.1.1 SHOULD limit).

## Members

- **Build** *(method)* - Serialise the given message to bytes.
- **Build** *(method)* - Serialise the given message to a stream.
